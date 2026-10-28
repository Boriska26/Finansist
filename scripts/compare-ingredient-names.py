#!/usr/bin/env python3
"""Compare cached models on fixed synthetic, names-only recipe requests."""
import hashlib
import json
from pathlib import Path
import re
import subprocess
import time

ROOT = Path(__file__).resolve().parent.parent
CACHE = ROOT / ".cache/llm"
OUTPUT = ROOT / "artifacts/ingredient-names"
RUNTIME = CACHE / "reference/bin/llama-completion"
MODELS = [
    ("1.7B-Q8", "Qwen3-1.7B-Q8_0.gguf", "061b54daade076b5d3362dac252678d17da8c68f07560be70818cace6590cb1a", True),
    ("4B-Q4", "Qwen3-4B-Instruct-2507-Q4_K_M.gguf", "3605803b982cb64aead44f6c1b2ae36e3acdb41d8e46c8a94c6533bc4c67e597", False),
]
REQUESTS = [
    ("borscht", "Какие ингредиенты нужны для обычного домашнего борща?"),
    ("buckwheat", "Какие ингредиенты нужны для гречки по-купечески?"),
    ("pilaf", "Какие ингредиенты нужны для плова?"),
    ("omelet", "Какие ингредиенты нужны для омлета с помидорами?"),
]


def main():
    if not RUNTIME.is_file():
        raise SystemExit("Нужен штатный llama-completion в .cache/llm/reference/bin.")
    # Validate every input before starting the comparison. No downloads or user data.
    for _, filename, digest, _ in MODELS:
        with (CACHE / filename).open("rb") as source:
            if hashlib.file_digest(source, "sha256").hexdigest() != digest:
                raise SystemExit(f"Неверный SHA-256: {filename}")
    OUTPUT.mkdir(parents=True, exist_ok=True)
    results = []
    for label, filename, _, hybrid in MODELS:
        for case, question in REQUESTS:
            request = question + " Напиши только названия продуктов, без количества, цен и пояснений."
            content = request + (" /no_think" if hybrid else "")
            prompt = f"<|im_start|>user\n{content}\n<|im_end|>\n<|im_start|>assistant\n"
            if hybrid:
                prompt += "<think>\n\n</think>\n\n"
            stem = f"{label}-{case}"
            prompt_file = OUTPUT / f"{stem}.prompt.txt"
            prompt_file.write_text(prompt, encoding="utf-8")
            command = [str(RUNTIME), "-m", str(CACHE / filename), "-f", str(prompt_file),
                       "--no-conversation", "-c", "4096", "-n", "384", "-t", "4",
                       "-b", "256", "-ub", "128", "-ngl", "0", "--temp", "0.4",
                       "--top-k", "20", "--top-p", "0.9", "--seed", "1337",
                       "--no-display-prompt", "--no-log-prefix", "--no-log-timestamps"]
            start = time.monotonic()
            log_file = OUTPUT / f"{stem}.log"
            timed_out = False
            with log_file.open("w", encoding="utf-8") as log:
                try:
                    process = subprocess.run(command, cwd=ROOT, stdout=log,
                                             stderr=subprocess.STDOUT, timeout=180)
                    exit_code = process.returncode
                except subprocess.TimeoutExpired:
                    timed_out, exit_code = True, None
            raw = log_file.read_text(encoding="utf-8")
            match = re.search(r"generate: n_ctx = [^\n]*\n", raw)
            answer = raw[match.end():] if match else ""
            answer = answer.split("[end of text]", 1)[0].split("common_perf_print:", 1)[0].strip()
            result = {"model": label, "case": case, "request": request,
                      "seconds": round(time.monotonic() - start, 2), "exit_code": exit_code,
                      "timed_out": timed_out, "ended": "[end of text]" in raw,
                      "answer": answer}
            results.append(result)
            (OUTPUT / "results.json").write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
            print(json.dumps(result, ensure_ascii=False), flush=True)
    if any(r["exit_code"] != 0 or not r["ended"] for r in results):
        raise SystemExit("Есть незавершённые/ошибочные ответы; это не успешный полный прогон.")


if __name__ == "__main__":
    main()
