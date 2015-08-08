﻿#region

using System;
using System.Collections.Generic;
using System.Linq;
using JosephM.Core.Extentions;
using JosephM.Core.FieldType;
using JosephM.Record.Application.Grid;
using JosephM.Record.Application.RecordEntry.Form;
using JosephM.Record.Application.Shared;
using JosephM.Record.IService;
using JosephM.Record.Query;

#endregion

namespace JosephM.Record.Application.RecordEntry.Field
{
    public abstract class ReferenceFieldViewModel<T> : FieldViewModel<T>, IReferenceFieldViewModel
    {
        protected ReferenceFieldViewModel(string fieldName, string fieldLabel, RecordEntryViewModelBase recordForm)
            : base(fieldName, fieldLabel, recordForm)
        {
            if(Value != null)
                SetEnteredTestWithoutClearingValue(GetValueName());
        }

        public void LoadLookupGrid()
        {
            LookupGridViewModel = new LookupGridViewModel(this, OnRecordSelected);
            XrmButton = new XrmButtonViewModel("Search", Search, ApplicationController);
        }

        public abstract string RecordTypeToLookup { get; set; }

        public void Search()
        {
            ApplicationController.DoOnAsyncThread(LoadRowsAsync);
        }

        public void OnRecordSelected(IRecord selectedRecord)
        {
            if (selectedRecord != null)
            {
                SetValue(selectedRecord);
                LookupGridVisible = false;
                var name = GetValueName();
                SetEnteredTestWithoutClearingValue(name);
            }
        }

        protected abstract string GetValueName();

        public abstract void SetValue(IRecord selectedRecord);

        protected void SetEnteredTestWithoutClearingValue(string recordName)
        {
            _enteredText = recordName;
            OnPropertyChanged("EnteredText");
        }

        public abstract IRecordService LookupService { get; }

        private string _enteredText;

        public string EnteredText
        {
            get { return _enteredText; }
            set
            {
                _enteredText = value;
                SetValue(null);
                OnPropertyChanged("EnteredText");
            }
        }

        private XrmButtonViewModel _xrmButton;

        public XrmButtonViewModel XrmButton
        {
            get { return _xrmButton; }
            set
            {
                _xrmButton = value;
                OnPropertyChanged("XrmButton");
            }
        }

        private bool _lookupGridVisible;

        public bool LookupGridVisible
        {
            get { return _lookupGridVisible; }
            set
            {
                _lookupGridVisible = value;
                OnPropertyChanged("LookupGridVisible");
            }
        }

        private LookupGridViewModel _lookupGridViewModel;

        public LookupGridViewModel LookupGridViewModel
        {
            get { return _lookupGridViewModel; }
            set
            {
                _lookupGridViewModel = value;
                OnPropertyChanged("LookupGridViewModel");
            }
        }

        protected int MaxRecordsForLookup
        {
            get { return 15; }
        }

        private bool _searching;

        public bool Searching
        {
            get { return _searching; }
            set
            {
                _searching = value;
                OnPropertyChanged("Searching");
            }
        }

        private readonly object _searchLock = new object();
        private void LoadRowsAsync()
        {
            lock (_searchLock)
            {
                LookupGridVisible = false;
                Searching = true;
                try
                {
                    var records = GetSearchResults();

                    DoOnMainThread(() =>
                    {
                        try
                        {
                            LookupGridViewModel.GridRecords = GridRowViewModel.LoadRows(records, LookupGridViewModel);
                            OnPropertyChanged("LookupGridViewModel");
                            Searching = false;
                            LookupGridVisible = LookupGridViewModel.GridRecords.Any();
                        }
                        catch (Exception)
                        {
                            Searching = false;
                            throw;
                        }
                    });
                }
                catch (Exception)
                {
                    Searching = false;
                    throw;
                }
            }
        }

        protected IEnumerable<Condition> GetConditions()
        {
            return FormService.GetLookupConditions(FieldName, RecordEntryViewModel.GetRecordType());
        }

        protected abstract IEnumerable<IRecord> GetSearchResults();

        public void SelectLookupGrid()
        {
            if (LookupGridVisible)
            {
                //move logical and key foucs to the grid view
                //have to add in the false focus first to ensire the ui properly processes the cvhange to focus on it
                LookupGridViewModel.IsFocused = false;
                LookupGridViewModel.MoveDown();
                LookupGridViewModel.IsFocused = true;
            }
        }
    }
}