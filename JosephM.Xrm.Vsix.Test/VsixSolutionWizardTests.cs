﻿using JosephM.Application.Desktop.Test;
using JosephM.XrmModule.SavedXrmConnections;
using JosephM.Xrm.Vsix.Application;
using JosephM.Xrm.Vsix.Module.PackageSettings;
using JosephM.Xrm.Vsix.Wizards;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Linq;

namespace JosephM.Xrm.Vsix.Test
{
    [TestClass]
    public class VsixSolutionWizardTests : JosephMVsixTests
    {
        [TestMethod]
        public void VsixSolutionWizardTest()
        {
            //okay so this wizard contains a dialog spawned different to the others
            //since it is loaded prior to the solution being created
            //this script just verifies the settings are netered into the dialog wihtout a crash with no visual studio solutoion context etc.


            var packageSettings = new XrmPackageSettings
            {
                 SolutionDynamicsCrmPrefix = "FAKE",
                 SolutionObjectPrefix = "Fake"
            };
            //okay spawn the wizards entry method with a fake controller
            var container = new VsixDependencyContainer();
            var applicationController = new FakeVsixApplicationController(container);
            XrmSolutionWizardBase.RunWizardSettingsEntry(packageSettings, applicationController);

            //fake applcation simply to use its navigation and entry methods
            var fakeVsixApplication = TestApplication.CreateTestApplication(applicationController);

            //okay so the package entry dialgo should redirect us to the conneciton entry when started as we don;t have a connection yet
            var dialog = fakeVsixApplication.GetNavigatedDialog<XrmPackageSettingsDialog>();
            var connectionEntryDialog = dialog.SubDialogs.First();
            var connectionEntry = fakeVsixApplication.GetSubObjectEntryViewModel(connectionEntryDialog);

            var connectionToEnter = GetXrmRecordConfiguration();
            fakeVsixApplication.EnterAndSaveObject(connectionToEnter, connectionEntry);

            //okay now the conneciton is entered it should navigate to the package settings entry
            var packageSettingsEntry = fakeVsixApplication.GetSubObjectEntryViewModel(dialog, index: 1);

            //we want to verify that the object had the settings passed into it as well as the lookup connection works
            packageSettingsEntry.GetBooleanFieldFieldViewModel(nameof(XrmPackageSettings.AddToSolution)).Value = true;
            var solutionPicklistField = packageSettingsEntry.GetLookupFieldFieldViewModel(nameof(XrmPackageSettings.Solution));
            solutionPicklistField.SelectedItem = solutionPicklistField.ItemsSource.First();

            var connectionsubGrid = packageSettingsEntry.GetEnumerableFieldViewModel(nameof(XrmPackageSettings.Connections));
            Assert.IsTrue(connectionsubGrid.GridRecords.Any());
            Assert.AreEqual(connectionToEnter.OrganizationUniqueName, connectionsubGrid.GridRecords.First().GetStringFieldFieldViewModel(nameof(SavedXrmRecordConfiguration.OrganizationUniqueName)).Value);

            packageSettingsEntry.SaveButtonViewModel.Invoke();

            Assert.IsTrue(packageSettings.Connections.Any());
            Assert.AreEqual(connectionToEnter.OrganizationUniqueName, packageSettings.Connections.First().OrganizationUniqueName);

            if(applicationController.GetObjects().Any())
                Assert.Inconclusive("Haven't verified closure of the form after entry instead of navigation to completion screen");
        }
    }
}
