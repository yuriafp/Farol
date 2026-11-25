namespace Farol.Engine.Indexing;

/// <summary>The index file was written again since it was opened: its symbol keys are another write's.</summary>
public sealed class IndexReplacedException : Exception
{
    public IndexReplacedException()
    {
    }

    public IndexReplacedException(string path)
        : base($"Index {path} was written again since it was opened.")
    {
    }

    public IndexReplacedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
