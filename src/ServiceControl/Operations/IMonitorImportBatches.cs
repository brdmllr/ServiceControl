namespace ServiceControl.Operations
{
    using System.Collections.Generic;

    public interface IMonitorImportBatches
    {
        void Accept(Dictionary<string, object> metadata);
        void FinalizeBatch();
    }
}