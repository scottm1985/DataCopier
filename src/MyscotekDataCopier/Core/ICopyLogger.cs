namespace MyscotekDataCopier.Core
{
    /// <summary>
    /// Receives every log line the engine produces. Called synchronously on the thread running
    /// <see cref="CopyEngine.Copy"/>; a UI implementation must marshal to its own thread.
    /// Messages already carry their tree indentation (two spaces per depth level).
    /// </summary>
    public interface ICopyLogger { void Log(LogLevel level, string message); }
}
