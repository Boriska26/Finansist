#!/usr/bin/env python3
"""Compare cached models on fixed synthetic, names-only recipe requests."""
import argparse
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
# filename, SHA-256, chat template, temperature, top-k, top-p, min-p.
MODELS = {
    "qwen17": ("1.7B-Q8", "Qwen3-1.7B-Q8_0.gguf", "061b54daade076b5d3362dac252678d17da8c68f07560be70818cace6590cb1a", "qwen-hybrid", 0.4, 20, 0.9, 0.05),
    "qwen4": ("4B-Q4", "Qwen3-4B-Instruct-2507-Q4_K_M.gguf", "3605803b982cb64aead44f6c1b2ae36e3acdb41d8e46c8a94c6533bc4c67e597", "qwen-instruct", 0.4, 20, 0.9, 0.05),
    "gemma3": ("Gemma3-4B-Q4", "gemma-3-4b-it-Q4_K_M.gguf", "04a43a22e8d2003deda5acc262f68ec1005fa76c735a9962a8c77042a74a7d19", "gemma3", 1.0, 64, 0.95, 0.0),
}
REQUESTS = [
    ("borscht", "Какие ингредиенты нужны для обычного домашнего борща?"),
    ("buckwheat", "Какие ингредиенты нужны для гречки по-купечески?"),
    ("pilaf", "Какие ингредиенты нужны для плова?"),
    ("omelet", "Какие ингредиенты нужны для омлета с помидорами?"),
]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--models", nargs="+", choices=MODELS, default=["qwen17", "qwen4"])
    parser.add_argument("--output", type=Path, default=OUTPUT)
    args = parser.parse_args()
    output = args.output.resolve()
    models = [MODELS[name] for name in args.models]
    if not RUNTIME.is_file():
        raise SystemExit("Нужен штатный llama-completion в .cache/llm/reference/bin.")
    # Validate every input before starting the comparison. No downloads or user data.
    for _, filename, digest, *_ in models:
        with (CACHE / filename).open("rb") as source:
            if hashlib.file_digest(source, "sha256").hexdigest() != digest:
                raise SystemExit(f"Неверный SHA-256: {filename}")
    output.mkdir(parents=True, exist_ok=True)
    results = []
    for label, filename, _, template, temperature, top_k, top_p, min_p in models:
        for case, question in REQUESTS:
            request = question + " Напиши только названия продуктов, без количества, цен и пояснений."
            if template == "gemma3":
                # The model tokenizer adds its BOS; including <bos> here would duplicate it.
                prompt = f"<start_of_turn>user\n{request}<end_of_turn>\n<start_of_turn>model\n"
            else:
                content = request + (" /no_think" if template == "qwen-hybrid" else "")
                prompt = f"<|im_start|>user\n{content}\n<|im_end|>\n<|im_start|>assistant\n"
                if template == "qwen-hybrid":
                    prompt += "<think>\n\n</think>\n\n"
            stem = f"{label}-{case}"
            prompt_file = output / f"{stem}.prompt.txt"
            prompt_file.write_text(prompt, encoding="utf-8")
            command = [str(RUNTIME), "-m", str(CACHE / filename), "-f", str(prompt_file),
                       "--no-conversation", "-c", "4096", "-n", "384", "-t", "4",
                       "-b", "256", "-ub", "128", "-ngl", "0", "--temp", str(temperature),
                       "--top-k", str(top_k), "--top-p", str(top_p), "--min-p", str(min_p), "--seed", "1337",
                       "--no-display-prompt", "--no-log-prefix", "--no-log-timestamps"]
            start = time.monotonic()
            log_file = output / f"{stem}.log"
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
                      "template": template, "temperature": temperature, "top_k": top_k, "top_p": top_p, "min_p": min_p,
                      "seconds": round(time.monotonic() - start, 2), "exit_code": exit_code,
                      "timed_out": timed_out, "ended": "[end of text]" in raw,
                      "answer": answer}
            results.append(result)
            (output / "results.json").write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
            print(json.dumps(result, ensure_ascii=False), flush=True)
    if any(r["exit_code"] != 0 or not r["ended"] for r in results):
        raise SystemExit("Есть незавершённые/ошибочные ответы; это не успешный полный прогон.")


if __name__ == "__main__":
    main()
