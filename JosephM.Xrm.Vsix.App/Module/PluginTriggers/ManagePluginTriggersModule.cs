﻿using System;
using JosephM.Application.Modules;
using JosephM.XrmModule.XrmConnection;
using JosephM.Xrm.Vsix.Module.PackageSettings;

namespace JosephM.Xrm.Vsix.Module.PluginTriggers
{
    [DependantModule(typeof(XrmPackageSettingsModule))]
    [DependantModule(typeof(XrmConnectionModule))]
    public class ManagePluginTriggersModule : OptionActionModule
    {
        public override string MainOperationName => "Manage Plugin Triggers";

        public override string MenuGroup => "Plugins";

        public override void DialogCommand()
        {
            ApplicationController.NavigateTo(typeof(ManagePluginTriggersDialog), null);
        }
    }
}

