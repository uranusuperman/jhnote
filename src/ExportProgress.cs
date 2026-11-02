namespace HwNote
{
    /// <summary>导出进度：CLI 忽略，GUI 用它刷新界面。</summary>
    public class ExportProgress
    {
        public int Total;
        public int Done;
        public string Current = "";
        public int Media;
        public int MediaFail;
        public string Message = "";
        public bool Finished;
    }
}
