﻿using JosephM.Application.ViewModel.RecordEntry.Form;
using JosephM.XRM.VSIX;
using JosephM.XRM.VSIX.Commands.PackageSettings;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Linq;

namespace JosephM.Xrm.Vsix.Test
{
    [TestClass]
    public class VsixRefreshSettingsDialogTests : JosephMVsixTests
    {
        [TestMethod]
        public void VsixRefreshSettingsDialogTest()
        {
            var fakeVisualStudioService = CreateVisualStudioService();

            var packageSettinns = new XrmPackageSettings();
            PopulateObject(packageSettinns);

            var dialog = new XrmPackageSettingDialog(CreateDialogController(), packageSettinns, fakeVisualStudioService, true, null);
            dialog.Controller.BeginDialog();

            var entryViewModel = (ObjectEntryViewModel)dialog.Controller.UiItems.First();
            Assert.IsTrue(entryViewModel.Validate());
            entryViewModel.OnSave();

            packageSettinns = new XrmPackageSettings();
            PopulateObject(packageSettinns);

            dialog = new XrmPackageSettingDialog(CreateDialogController(), packageSettinns, fakeVisualStudioService, true, XrmRecordService);
            dialog.Controller.BeginDialog();

            entryViewModel = (ObjectEntryViewModel)dialog.Controller.UiItems.First();
            Assert.IsTrue(entryViewModel.Validate());
            entryViewModel.OnSave();
        }
    }
}
