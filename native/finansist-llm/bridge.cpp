#include "llama.h"
#include <algorithm>
#include <cstring>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

using cancel_fn = int (*)();
static void quiet(enum ggml_log_level, const char *, void *) {}
static std::once_flag initialized;
struct cancellation { cancel_fn callback; };
static bool aborted(void * data) { auto * c = static_cast<cancellation *>(data); return c->callback && c->callback() != 0; }
static bool loading(float, void * data) { return !aborted(data); }

// One bounded request per call. All model/context/sampler memory is released on return.
// Codes: 0 success, 1 cancellation, 2 model failure, 3 execution failure, 4 incomplete output.
static int generate(
 const char * path, const char * prompt, const char * grammar, cancel_fn cancel, char * output, int capacity, float temperature, int top_k, float top_p, int max_tokens) noexcept {
 try {
  std::call_once(initialized, [] { llama_log_set(quiet, nullptr); ggml_log_set(quiet, nullptr); llama_backend_init(); });
  if (!path || !prompt || !grammar || !output || capacity < 2) return 3;
  output[0] = 0;
  cancellation cancellation_data{cancel};
  auto params = llama_model_default_params(); params.n_gpu_layers = 0;
  params.progress_callback = loading; params.progress_callback_user_data = &cancellation_data;
  std::unique_ptr<llama_model, decltype(&llama_model_free)> model(llama_model_load_from_file(path, params), llama_model_free);
  if (aborted(&cancellation_data)) return 1;
  if (!model) return 2;
  auto context_params = llama_context_default_params();
  context_params.n_ctx = 4096; context_params.n_batch = 256; context_params.n_ubatch = 128;
  context_params.n_threads = 4; context_params.n_threads_batch = 4;
  context_params.abort_callback = aborted; context_params.abort_callback_data = &cancellation_data;
  std::unique_ptr<llama_context, decltype(&llama_free)> context(llama_init_from_model(model.get(), context_params), llama_free);
  if (!context) return aborted(&cancellation_data) ? 1 : 3;
  const auto * vocab = llama_model_get_vocab(model.get());
  const int length = static_cast<int>(std::strlen(prompt));
  int count = -llama_tokenize(vocab, prompt, length, nullptr, 0, true, true);
  if (count <= 0 || count > 1800) return 3;
  std::vector<llama_token> tokens(count);
  if (llama_tokenize(vocab, prompt, length, tokens.data(), count, true, true) != count) return 3;
  std::unique_ptr<llama_batch_ext, decltype(&llama_batch_ext_free)> batch(llama_batch_ext_init(context.get()), llama_batch_ext_free);
  if (!batch) return 3;
  for (int offset = 0; offset < count; offset += 256) {
   if (aborted(&cancellation_data)) return 1;
   llama_batch_ext_clear(batch.get());
   const int chunk = std::min(256, count - offset);
   for (int j = 0; j < chunk; ++j) {
    const int idx = llama_batch_ext_add_token(batch.get(), 0, tokens[offset + j]);
    llama_pos position = offset + j;
    if (idx < 0 || !llama_batch_ext_set_pos(batch.get(), idx, &position)) return 3;
   }
   llama_batch_ext_set_output_logits(batch.get(), chunk - 1, true);
   if (llama_process(context.get(), LLAMA_PROCESS_TYPE_DECODE, batch.get()) != 0) return aborted(&cancellation_data) ? 1 : 3;
  }
  std::unique_ptr<llama_sampler, decltype(&llama_sampler_free)> sampler(llama_sampler_chain_init(llama_sampler_chain_default_params()), llama_sampler_free);
  if (grammar[0]) {
   auto * constrained = llama_sampler_init_grammar(vocab, grammar, "root");
   if (!constrained) return 3;
   llama_sampler_chain_add(sampler.get(), constrained);
  }
  llama_sampler_chain_add(sampler.get(), llama_sampler_init_top_k(top_k));
  llama_sampler_chain_add(sampler.get(), llama_sampler_init_top_p(top_p, 1));
  llama_sampler_chain_add(sampler.get(), llama_sampler_init_temp(temperature));
  llama_sampler_chain_add(sampler.get(), llama_sampler_init_dist(1337));
  std::string result;
  for (int i = 0; i < max_tokens; ++i) {
   if (aborted(&cancellation_data)) return 1;
   llama_token token = llama_sampler_sample(sampler.get(), context.get(), -1);
   if (llama_vocab_is_eog(vocab, token)) {
    if (result.size() + 1 > static_cast<size_t>(capacity)) return 4;
    std::memcpy(output, result.c_str(), result.size() + 1); return 0;
   }
   char piece[1024]; int size = llama_token_to_piece(vocab, token, piece, sizeof(piece), 0, true);
   if (size < 0 || result.size() + size + 1 >= static_cast<size_t>(capacity)) return 4;
   result.append(piece, size);
   llama_batch_ext_clear(batch.get());
   const int idx = llama_batch_ext_add_token(batch.get(), 0, token);
   llama_pos position = count + i;
   if (idx < 0 || !llama_batch_ext_set_pos(batch.get(), idx, &position)) return 3;
   llama_batch_ext_set_output_logits(batch.get(), idx, true);
   if (llama_process(context.get(), LLAMA_PROCESS_TYPE_DECODE, batch.get()) != 0) return aborted(&cancellation_data) ? 1 : 3;
  }
  return 4;
 } catch (...) { return 3; }
}

// Preserve the old ABI for the reproducible Qwen comparisons.
extern "C" __attribute__((visibility("default"))) int fin_generate(
 const char * path, const char * prompt, const char * grammar, cancel_fn cancel, char * output, int capacity) noexcept {
 return generate(path, prompt, grammar, cancel, output, capacity, .4f, 20, .9f, 2048);
}
extern "C" __attribute__((visibility("default"))) int fin_generate_gemma(
 const char * path, const char * prompt, cancel_fn cancel, char * output, int capacity, int max_tokens) noexcept {
 if (max_tokens < 1 || max_tokens > 1024) return 3;
 return generate(path, prompt, "", cancel, output, capacity, 1.f, 64, .95f, max_tokens);
}
