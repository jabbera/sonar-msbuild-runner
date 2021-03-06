﻿//-----------------------------------------------------------------------
// <copyright file="SonarWebServices.cs" company="SonarSource SA and Microsoft Corporation">
//   Copyright (c) SonarSource SA and Microsoft Corporation.  All rights reserved.
//   Licensed under the MIT License. See License.txt in the project root for license information.
// </copyright>
//-----------------------------------------------------------------------

using Newtonsoft.Json.Linq;
using SonarQube.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;

namespace SonarQube.TeamBuild.PreProcessor
{
    public sealed class SonarWebService : ISonarQubeServer, IDisposable
    {
        private readonly string server;
        private readonly IDownloader downloader;
        private readonly ILogger logger;

        public SonarWebService(IDownloader downloader, string server, ILogger logger)
        {
            if (downloader == null)
            {
                throw new ArgumentNullException("downloader");
            }
            if (string.IsNullOrWhiteSpace(server))
            {
                throw new ArgumentNullException("server");
            }
            if (logger == null)
            {
                throw new ArgumentNullException("logger");
            }

            this.downloader = downloader;
            this.server = server.EndsWith("/", StringComparison.OrdinalIgnoreCase) ? server.Substring(0, server.Length - 1) : server;
            this.logger = logger;
        }

        #region ISonarQubeServer interface

        public bool TryGetQualityProfile(string projectKey, string projectBranch, string language, out string qualityProfile)
        {
            string projectId = GetProjectIdentifier(projectKey, projectBranch);

            string contents;
            var ws = GetUrl("/api/profiles/list?language={0}&project={1}", language, projectId);
            if (!this.downloader.TryDownloadIfExists(ws, out contents))
            {
                ws = GetUrl("/api/profiles/list?language={0}", language);
                contents = this.downloader.Download(ws);
            }
            var profiles = JArray.Parse(contents);

            if (!profiles.Any())
            {
                qualityProfile = null;
                return false;
            }

            var profile = profiles.Count > 1 ? profiles.Where(p => "True".Equals(p["default"].ToString())).Single() : profiles.Single();
            qualityProfile = profile["name"].ToString();
            return true;
        }

        public IEnumerable<string> GetActiveRuleKeys(string qualityProfile, string language, string repository)
        {
            var ws = GetUrl("/api/profiles/index?language={0}&name={1}", language, qualityProfile);
            var contents = this.downloader.Download(ws);

            var profiles = JArray.Parse(contents);
            var rules = profiles.Single()["rules"];
            if (rules == null) {
                return Enumerable.Empty<string>();
            }
            
            return rules
                .Where(r => repository.Equals(r["repo"].ToString()))
                .Select(
                r =>
                {
                    var checkIdParameter = r["params"] == null ? null : r["params"].Where(p => "CheckId".Equals(p["key"].ToString())).SingleOrDefault();
                    return checkIdParameter == null ? r["key"].ToString() : checkIdParameter["value"].ToString();
                });
        }

        public IDictionary<string, string> GetInternalKeys(string repository)
        {
            var ws = GetUrl("/api/rules/search?f=internalKey&ps={0}&repositories={1}", int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), repository);
            var contents = this.downloader.Download(ws);

            var rules = JObject.Parse(contents);
            var keysToIds = rules["rules"]
                .Where(r => r["internalKey"] != null)
                .ToDictionary(r => r["key"].ToString(), r => r["internalKey"].ToString());

            return keysToIds;
        }

        /// <summary>
        /// Retrieves project properties from the server.
        /// 
        /// Will fail with an exception if the downloaded return from the server is not a JSON array.
        /// </summary>
        /// <param name="projectKey">The SonarQube project key to retrieve properties for.</param>
        /// <param name="projectBranch">The SonarQube project branch to retrieve properties for (optional).</param>
        /// <returns>A dictionary of key-value property pairs.</returns>
        public IDictionary<string, string> GetProperties(string projectKey, string projectBranch = null)
        {
            if (string.IsNullOrWhiteSpace(projectKey))
            {
                throw new ArgumentNullException("projectKey");
            }

            string projectId = GetProjectIdentifier(projectKey, projectBranch);
           
            string ws = GetUrl("/api/properties?resource={0}", projectId);
            this.logger.LogDebug(Resources.MSG_FetchingProjectProperties, projectId, ws);
            var contents = this.downloader.Download(ws);

            var properties = JArray.Parse(contents);
            var result = properties.ToDictionary(p => p["key"].ToString(), p => p["value"].ToString());

            // http://jira.sonarsource.com/browse/SONAR-5891 
            if (!result.ContainsKey("sonar.cs.msbuild.testProjectPattern"))
            {
                result["sonar.cs.msbuild.testProjectPattern"] = SonarProperties.DefaultTestProjectPattern;
            }

            return result;
        }

        // TODO Should be replaced by calls to api/languages/list after min(SQ version) >= 5.1
        public IEnumerable<string> GetInstalledPlugins()
        {
            var ws = GetUrl("/api/updatecenter/installed_plugins");
            var contents = this.downloader.Download(ws);

            var plugins = JArray.Parse(contents);

            return plugins.Select(plugin => plugin["key"].ToString());
        }

        /// <summary>
        /// Attempts to download the quality profile in the specified format
        /// </summary>
        public bool TryGetProfileExport(string qualityProfile, string language, string format, out string content)
        {
            if (string.IsNullOrWhiteSpace(qualityProfile))
            {
                throw new ArgumentNullException("qualityProfile");
            }
            if (string.IsNullOrWhiteSpace(language))
            {
                throw new ArgumentNullException("language");
            }
            if (string.IsNullOrWhiteSpace(format))
            {
                throw new ArgumentNullException("format");
            }

            string url = GetUrl("/profiles/export?format={0}&language={1}&name={2}", format, language, qualityProfile);
            bool success = this.downloader.TryDownloadIfExists(url, out content);
            return success;
        }

        public bool TryDownloadEmbeddedFile(string pluginKey, string embeddedFileName, string targetDirectory)
        {
            if (string.IsNullOrWhiteSpace(pluginKey))
            {
                throw new ArgumentNullException("pluginKey");
            }
            if (string.IsNullOrWhiteSpace(embeddedFileName))
            {
                throw new ArgumentNullException("embeddedFileName");
            }
            if (string.IsNullOrWhiteSpace(targetDirectory))
            {
                throw new ArgumentNullException("targetDirectory");
            }

            string url = GetUrl("/static/{0}/{1}", pluginKey, embeddedFileName);

            string targetFilePath = Path.Combine(targetDirectory, embeddedFileName);

            logger.LogDebug(Resources.MSG_DownloadingZip, embeddedFileName, url, targetDirectory);
            bool success = this.downloader.TryDownloadFileIfExists(url, targetFilePath);
            return success;
        }

        #endregion

        #region Private methods

        /// <summary>
        /// Concatenates project key and branch into one string.
        /// </summary>
        /// <param name="projectKey">Unique project key</param>
        /// <param name="projectBranch">Specified branch of the project. Null if no branch to be specified.</param>
        /// <returns>A correctly formatted branch-specific identifier (if appropriate) for a given project.</returns>
        private static string GetProjectIdentifier(string projectKey, string projectBranch = null)
        {
            string projectId = projectKey;
            if (!string.IsNullOrWhiteSpace(projectBranch))
            {
                projectId = projectKey + ":" + projectBranch;
            }

            return projectId;
        }

        private string GetUrl(string format, params string[] args)
        {
            var queryString = string.Format(System.Globalization.CultureInfo.InvariantCulture, format, args.Select(a => WebUtility.UrlEncode(a)).ToArray());
            if (!queryString.StartsWith("/", StringComparison.OrdinalIgnoreCase))
            {
                queryString = '/' + queryString;
            }
            return this.server + queryString;
        }

        #endregion

        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    Utilities.SafeDispose(this.downloader);
                }

                disposedValue = true;
            }
        }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
        }

        #endregion

    }
}
