﻿using JosephM.Application.Desktop.Module.ServiceRequest;
using JosephM.Application.ViewModel.Dialog;
using JosephM.Record.Xrm.XrmRecord;


namespace JosephM.Deployment.ImportXml
{
    public class ImportXmlDialog :
        ServiceRequestDialog
            <ImportXmlService, ImportXmlRequest,
                ImportXmlResponse, DataImportResponseItem>
    {
        public ImportXmlDialog(ImportXmlService service,
            IDialogController dialogController, XrmRecordService lookupService)
            : base(service, dialogController, lookupService)
        {
        }
    }
}