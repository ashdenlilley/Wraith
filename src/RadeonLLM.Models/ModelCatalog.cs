namespace RadeonLLM.Models;

public sealed record CatalogEntry(string Name, string Source, double SizeGb, string Note);

/// <summary>Curated GGUFs that suit a 16 GB card. Sources are Hugging Face owner/repo/file shorthand.</summary>
public static class ModelCatalog
{
    public static readonly IReadOnlyList<CatalogEntry> Entries =
    [
        new("Qwen2.5 Coder 14B Instruct Q5_K_M", "Qwen/Qwen2.5-Coder-14B-Instruct-GGUF/qwen2.5-coder-14b-instruct-q5_k_m.gguf", 9.8, "Coding, best fit for 16 GB"),
        new("Qwen2.5 Coder 7B Instruct Q5_K_M", "Qwen/Qwen2.5-Coder-7B-Instruct-GGUF/qwen2.5-coder-7b-instruct-q5_k_m.gguf", 5.1, "Coding, fast"),
        new("DeepSeek R1 Distill Qwen 14B Q4_K_M", "bartowski/DeepSeek-R1-Distill-Qwen-14B-GGUF/DeepSeek-R1-Distill-Qwen-14B-Q4_K_M.gguf", 8.4, "Reasoning"),
        new("Llama 3.1 8B Instruct Q5_K_M", "bartowski/Meta-Llama-3.1-8B-Instruct-GGUF/Meta-Llama-3.1-8B-Instruct-Q5_K_M.gguf", 5.3, "General purpose"),
        new("Gemma 3 12B Instruct Q5_K_M", "unsloth/gemma-3-12b-it-GGUF/gemma-3-12b-it-Q5_K_M.gguf", 7.9, "General purpose"),
        new("Qwen2.5 0.5B Instruct Q4_K_M", "Qwen/Qwen2.5-0.5B-Instruct-GGUF/qwen2.5-0.5b-instruct-q4_k_m.gguf", 0.5, "Tiny, for testing"),
    ];
}
