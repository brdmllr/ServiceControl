﻿namespace ServiceControlInstaller.Engine.Validation
{
    public interface IContainTransportInfo
    {
        string TransportPackage { get; set; }
        string ErrorQueue { get; set; }
        string AuditQueue { get; set; }
        string ErrorLogQueue { get; set; }
        string AuditLogQueue { get; set; }
        string ConnectionString { get; set; }
    }
}
