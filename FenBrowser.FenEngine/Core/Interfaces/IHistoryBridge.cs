namespace FenBrowser.FenEngine.Core.Interfaces
{
    public interface IHistoryBridge
    {
        void PushState(object state, string title, string url);
        void ReplaceState(object state, string title, string url);
        void Go(int delta);
        int Length { get; }
        object State { get; }
    }
}
