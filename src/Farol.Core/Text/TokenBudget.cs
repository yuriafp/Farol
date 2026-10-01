namespace Farol.Core.Text;

/// <summary>Cheap token estimate (about four characters per token) used to keep responses inside a budget.</summary>
public static class TokenBudget
{
    public const int CharsPerToken = 4;
    public const int DefaultTokens = 1500;
    public const int MinTokens = 200;
    public const int MaxTokens = 8000;

    public static int Estimate(string text) => (text.Length + CharsPerToken - 1) / CharsPerToken;

    /// <summary>Clamps a caller-provided budget into the supported range; zero or negative means the default.</summary>
    public static int Clamp(int requested) =>
        requested <= 0 ? DefaultTokens : Math.Clamp(requested, MinTokens, MaxTokens);
}
