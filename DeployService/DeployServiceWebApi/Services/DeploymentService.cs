﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using DeploymentJobs.DataAccess;
using DeploymentSettings.Models;
using DeployService.Common.Exceptions;
using DeployServiceWebApi.Exceptions;
using DeployServiceWebApi.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog.Events;

namespace DeployServiceWebApi.Services
{
    public interface IDeploymentService
    {
        bool TryRunJobIfNotInProgress(
            string project,
            string service,
            List<DeploymentScript> scripts,
            out DeploymentJob job);

        void CancelJob(string jobId);
    }

    public class DeploymentService : IDeploymentService
    {
        private readonly ILogger<DeploymentService> _logger;
        private readonly IDeploymentJobDataAccess _jobsDataAccess;

        public DeploymentService(
            ILogger<DeploymentService> logger,
            IDeploymentJobDataAccess jobsDataAccess)
        {
            _logger = logger;
            _jobsDataAccess = jobsDataAccess;
        }

        public void CancelJob(string jobId)
        {
            try
            {
                _jobsDataAccess.CancelJob(jobId);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to cancel job with id ${jobId}.", ex);
                throw;
            }
        }

        public bool TryRunJobIfNotInProgress(
            string project,
            string service,
            List<DeploymentScript> scripts,
            out DeploymentJob job)
        {
            if (!_jobsDataAccess.TryCreateIfVacant(project, service, out job))
            {
                return false;
            }

            var jobId = job.Id;
            Task.Run(() => RunJob(jobId, scripts));

            return true;
        }

        private void RunJob(string jobId, List<DeploymentScript> scripts)
        {
            try
            {
                foreach (var script in scripts)
                {
                    if (!File.Exists(script.Path))
                    {
                        throw new DeploymentException($"File cannot be found: {script.Path}");
                    };

                    // check if the job hasn't been cancelled and exit if it has.
                    if (_jobsDataAccess.CheckJobStatus(jobId, DeploymentJobStatus.CANCELLED))
                    {
                        _logger.LogInformation($"Job with id ${jobId} has been cancelled. Exiting deployment.");
                        return;
                    }

                    ExecuteScript(script, jobId);
                }

                _jobsDataAccess.SetSuccess(jobId);
            }
            catch (Exception ex)
            {
                var errorMessage = $"Error running deployables: {ex.Message}";
                _logger.LogError(errorMessage, ex);

                // set fail if haven't already been set while running deployment scripts.
                if (!_jobsDataAccess.CheckJobStatus(jobId, DeploymentJobStatus.FAIL))
                {
                    _jobsDataAccess.SetFail(jobId, ex.Message);
                }
            }
        }
        
        private void ExecuteScript(DeploymentScript script, string jobId)
        {
            var proc = new Process
            {
                StartInfo =
                {
                    FileName = (new FileInfo(script.Path)).FullName,
                    Arguments = script.Arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            _logger.LogInformation($"Starting script {Path.GetFileName(script.Path)}");

            proc.Start();

            _jobsDataAccess.SetInProgress(jobId, $"Running script {Path.GetFileName(script.Path)}", proc);

            // read output and error streams async
            proc.OutputDataReceived += (sender, eventArgs) => LogIfNotEmpty(jobId, eventArgs.Data);
            proc.ErrorDataReceived += (sender, eventArgs) => LogIfNotEmpty(jobId, eventArgs.Data, LogEventLevel.Error);

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            proc.WaitForExit();

            // check if the job has failed during deployment.
            if (proc.ExitCode != 0 || _jobsDataAccess.CheckJobStatus(jobId, DeploymentJobStatus.FAIL))
            {
                var message = $"Error running script {Path.GetFileName(script.Path)}, exit code: {proc.ExitCode}";
                throw new DeploymentException(message);
            }
        }

        private void LogIfNotEmpty(
            string jobId,
            string logMessage,
            LogEventLevel level = LogEventLevel.Information)
        {
            if (string.IsNullOrWhiteSpace(logMessage)) return;

            // catch deployer-specific error messages when no error code is returned.
            if (MessageHasDeploymentErrors(logMessage)) 
            {
                _jobsDataAccess.SetFail(jobId, "Deploy failed. Check logs for more information.");
            }

            if (level == LogEventLevel.Error)
            {
                _logger.LogError(logMessage);
                return;
            }

            _logger.LogInformation(logMessage);
        }

        private bool MessageHasDeploymentErrors(string message)
        {
            var messageLowerCase = message.ToLower();
            return messageLowerCase.Contains("deploy failed") || messageLowerCase.Contains("there were errors");
        }
    }
}
