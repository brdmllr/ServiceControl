namespace ServiceControl.Operations
{
    using System.Collections.Generic;

    public interface IMonitorImportBatches
    {
        void Accept(IEnumerable<Dictionary<string, object>> batch);
    }
}