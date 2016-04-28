﻿using Microsoft.VisualStudio.Services.Agent.Util;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Services.Agent.Worker.TestResults
{
    public class ResultsCommands : AgentService, ICommandExtension
    {
        private IExecutionContext _executionContext;
        //publish test results inputs
        private List<string> _testResultFiles;
        private string _testRunner;
        private bool _mergeResults;
        private string _platform;
        private string _configuration;
        private string _runTitle;
        private bool _publishRunLevelAttachments;

        public void ProcessCommand(IExecutionContext context, Command command)
        {
            if (string.Equals(command.Event, WellKnownResultsCommand.PublishTestResults, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    ArgUtil.NotNull(context, nameof(context));
                    _executionContext = context;

                    LoadPublishTestResultsInputs(command.Properties);

                    // publish test results to VSO using the reader based on the test runner specified
                    IResultReader resultsReader = GetTestResultReader(_testRunner);
                    resultsReader.AddResultsFileToRunLevelAttachments = _publishRunLevelAttachments;
                    PublishTestResults(resultsReader);
                }
                catch (Exception ex)
                {
                    //Do not fail the task.
                    string message = ex.Message;
                    if (ex.InnerException != null)
                    {
                        message += Environment.NewLine;
                        message += ex.InnerException.Message;
                    }
                    context.Warning(StringUtil.Loc("FailedToPublishTestResults", message));
                }
            }
            else
            {
                throw new Exception(StringUtil.Loc("ResultsCommandNotFound", command.Event));
            }
        }

        public Type ExtensionType
        {
            get
            {
                return typeof(ICommandExtension);
            }
        }

        public string CommandArea
        {
            get
            {
                return "results";
            }
        }

        private void PublishTestResults(IResultReader resultReader)
        {
            Client.VssConnection connection = WorkerUtilies.GetVssConnection(_executionContext);

            string teamProject = _executionContext.Variables.System_TeamProject;
            string owner = _executionContext.Variables.Build_RequestedFor;
            string buildUri = _executionContext.Variables.Build_BuildUri;
            int buildId = _executionContext.Variables.Build_BuildId ?? 0;

            //Temporary fix to support publish in RM scenarios where there might not be a valid Build ID associated.
            //TODO: Make a cleaner fix after TCM User Story 401703 is completed.
            if (buildId == 0)
            {
                _platform = _configuration = null;
            }

            string releaseUri = null;
            string releaseEnvironmentUri = null;

            // Check to identify if we are in the Release management flow; if not, then release fields will be kept null while publishing to TCM 
            if (!string.IsNullOrWhiteSpace(_executionContext.Variables.Release_ReleaseUri))
            {
                releaseUri = _executionContext.Variables.Release_ReleaseUri;
                releaseEnvironmentUri = _executionContext.Variables.Release_ReleaseEnvironmentUri;
            }

            TestRunContext runContext = new TestRunContext(
                                                owner,
                                                _platform,
                                                _configuration,
                                                buildId,
                                                buildUri,
                                                releaseUri,
                                                releaseEnvironmentUri
                                                );

            var publisher = HostContext.GetService<ITestRunPublisher>();
            publisher.InitializePublisher(_executionContext, connection, teamProject, runContext, resultReader);
            var commandContext = HostContext.CreateService<IAsyncCommandContext>();
            commandContext.InitializeCommandContext(_executionContext, StringUtil.Loc("PublishTestResults"));

            if (_mergeResults)
            {
                commandContext.Task = PublishAllTestResultsToSingleTestRunAsync(_testResultFiles, publisher, buildId, runContext);
            }
            else
            {
                commandContext.Task = PublishToNewTestRunPerTestResultFileAsync(_testResultFiles, publisher);
            }
            _executionContext.AsyncCommands.Add(commandContext);
        }

        /// <summary>
        /// Publish single test run
        /// </summary>
        private async Task PublishAllTestResultsToSingleTestRunAsync(List<string> resultFiles, ITestRunPublisher publisher, int buildId, TestRunContext runContext)
        {
            DateTime startTime = DateTime.Now; //use local time since TestRunData defaults to local times
            TimeSpan totalTestCaseDuration = TimeSpan.Zero;
            List<string> runAttachments = new List<string>();
            List<TestCaseResultData> runResults = new List<TestCaseResultData>();

            //read results from each file
            foreach (string resultFile in resultFiles)
            {
                //test case results
                TestRunData resultFileRunData = publisher.ReadResultsFromFile(resultFile);

                if (resultFileRunData != null && resultFileRunData.Results != null && resultFileRunData.Results.Length > 0)
                {
                    foreach (TestCaseResultData tcResult in resultFileRunData.Results)
                    {
                        int durationInMs = 0;
                        int.TryParse(tcResult.DurationInMs, out durationInMs);
                        totalTestCaseDuration = totalTestCaseDuration.Add(TimeSpan.FromMilliseconds(durationInMs));
                    }
                    runResults.AddRange(resultFileRunData.Results);

                    //run attachments
                    if (resultFileRunData.Attachments != null)
                    {
                        runAttachments.AddRange(resultFileRunData.Attachments);
                    }
                }
                else
                {
                    _executionContext.Warning(StringUtil.Loc("InvalidResultFiles", resultFile));
                }
            }

            string runName = string.IsNullOrWhiteSpace(_runTitle)
                ? string.Format("{0}_TestResults_{1}", _testRunner, buildId)
                : _runTitle;

            //creat test run
            TestRunData testRunData = new TestRunData(
                name: runName,
                startedDate: startTime.ToString("o"),
                completedDate: startTime.Add(totalTestCaseDuration).ToString("o"),
                state: "InProgress",
                isAutomated: true,
                buildId: runContext != null ? runContext.BuildId : 0,
                buildFlavor: runContext != null ? runContext.Configuration : string.Empty,
                buildPlatform: runContext != null ? runContext.Platform : string.Empty
                );

            testRunData.Attachments = runAttachments.ToArray();

            //publish run if there are results.
            if (runResults.Count > 0)
            {
                publisher.StartTestRunAsync(testRunData).Wait();
                publisher.AddResultsAsync(runResults.ToArray()).Wait();
                await publisher.EndTestRunAsync(publishAttachmentsAsArchive: true);
            }
        }

        /// <summary>
        /// Publish separate test run for each result file that has results.
        /// </summary>
        private async Task PublishToNewTestRunPerTestResultFileAsync(List<string> resultFiles, ITestRunPublisher publisher)
        {
            int runCount = 1;
            // Publish separate test run for each result file that has results.
            foreach (string resultFile in resultFiles)
            {
                string runName = null;
                if (!string.IsNullOrWhiteSpace(_runTitle))
                {
                    runName = resultFiles.Count > 1 ? string.Format(CultureInfo.InvariantCulture, "{0} {1}", _runTitle, runCount++) : _runTitle;
                }

                TestRunData testRunData = publisher.ReadResultsFromFile(resultFile, runName);

                if (testRunData != null && testRunData.Results != null && testRunData.Results.Length > 0)
                {
                    publisher.StartTestRunAsync(testRunData).Wait();
                    publisher.AddResultsAsync(testRunData.Results).Wait();
                    await publisher.EndTestRunAsync();
                }
                else
                {
                    _executionContext.Warning(StringUtil.Loc("InvalidResultFiles", resultFile));
                }
            }
        }

        private IResultReader GetTestResultReader(string testRunner)
        {
            var extensionManager = HostContext.GetService<IExtensionManager>();
            IResultReader reader = (extensionManager.GetExtensions<IResultReader>()).FirstOrDefault(x => testRunner.Equals(x.Name, StringComparison.OrdinalIgnoreCase));

            if (reader == null)
            {
                throw new ArgumentException("Unknown Test Runner.");
            }
            return reader;
        }

        private void LoadPublishTestResultsInputs(Dictionary<string, string> eventProperties)
        {
            // Validate input test results files
            string resultFilesInput;
            eventProperties.TryGetValue(PublishTestResultsEventProperties.ResultFiles, out resultFilesInput);
            if (string.IsNullOrEmpty(resultFilesInput) || resultFilesInput.Split(',').Count() == 0)
            {
                throw new ArgumentException(StringUtil.Loc("ArgumentNeeded", "TestResults"));
            }
            _testResultFiles = resultFilesInput.Split(',').ToList<string>();

            //validate testrunner input
            eventProperties.TryGetValue(PublishTestResultsEventProperties.Type, out _testRunner);
            if (string.IsNullOrEmpty(_testRunner))
            {
                throw new ArgumentException(StringUtil.Loc("ArgumentNeeded", "Testrunner"));
            }

            string mergeResultsInput;
            eventProperties.TryGetValue(PublishTestResultsEventProperties.MergeResults, out mergeResultsInput);
            if (string.IsNullOrEmpty(mergeResultsInput) || !bool.TryParse(mergeResultsInput, out _mergeResults))
            {
                // if no proper input is provided by default we merge test results
                _mergeResults = true;
            }

            eventProperties.TryGetValue(PublishTestResultsEventProperties.Platform, out _platform);
            if (_platform == null)
            {
                _platform = string.Empty;
            }

            eventProperties.TryGetValue(PublishTestResultsEventProperties.Configuration, out _configuration);
            if (_configuration == null)
            {
                _configuration = string.Empty;
            }

            eventProperties.TryGetValue(PublishTestResultsEventProperties.RunTitle, out _runTitle);
            if (_runTitle == null)
            {
                _runTitle = string.Empty;
            }

            string publishRunAttachmentsInput;
            eventProperties.TryGetValue(PublishTestResultsEventProperties.PublishRunAttachments, out publishRunAttachmentsInput);
            if (string.IsNullOrEmpty(publishRunAttachmentsInput) || !bool.TryParse(publishRunAttachmentsInput, out _publishRunLevelAttachments))
            {
                // if no proper input is provided by default we publish attachments.
                _publishRunLevelAttachments = true;
            }
        }
    }

    internal static class WellKnownResultsCommand
    {
        public static readonly string PublishTestResults = "publish";
    }

    internal static class PublishTestResultsEventProperties
    {
        public static readonly string Type = "type";
        public static readonly string MergeResults = "mergeResults";
        public static readonly string Platform = "platform";
        public static readonly string Configuration = "config";
        public static readonly string RunTitle = "runTitle";
        public static readonly string PublishRunAttachments = "publishRunAttachments";
        public static readonly string ResultFiles = "resultFiles";
    }
}