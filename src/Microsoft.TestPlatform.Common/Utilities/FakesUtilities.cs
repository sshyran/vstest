﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.TestPlatform.Common.Utilities
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Reflection;
    using System.Xml;

    using Microsoft.VisualStudio.TestPlatform.ObjectModel;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel.Utilities;

    /// <summary>
    /// Provides helper to configure run settings for Fakes. Works even when Fakes are not installed on the machine.
    /// </summary>
    public static class FakesUtilities
    {
        private const string ConfiguratorAssemblyQualifiedName = "Microsoft.VisualStudio.TestPlatform.Fakes.FakesDataCollectorConfiguration";

        private const string ConfiguratorMethodName = "GetDataCollectorSettingsOrDefault";

        private const string FakesConfiguratorAssembly = "Microsoft.VisualStudio.TestPlatform.Fakes, Version=16.0.0.0, Culture=neutral";

        /// <summary>
        /// Dynamically compute the Fakes data collector settings, given a set of test assemblies
        /// </summary>
        /// <param name="sources">test sources</param>
        /// <param name="runSettingsXml">runsettings</param>
        /// <returns>updated runsettings for fakes</returns>
        public static string GenerateFakesSettingsForRunConfiguration(string[] sources, string runSettingsXml)
        {
            if (sources == null)
            {
                throw new ArgumentNullException(nameof(sources));
            }

            if (runSettingsXml == null)
            {
                throw new ArgumentNullException(nameof(runSettingsXml));
            }

            var doc = new XmlDocument();
            using (var xmlReader = XmlReader.Create(
                new StringReader(runSettingsXml),
                new XmlReaderSettings() { CloseInput = true }))
            {
                doc.Load(xmlReader);
            }

            return !TryAddFakesDataCollectorSettings(doc, sources, GetFramework(runSettingsXml)) 
                ? runSettingsXml 
                : doc.OuterXml;
        }

        private static FrameworkVersion GetFramework(string runSettingsXml)
        {
            var config = XmlRunSettingsUtilities.GetRunConfigurationNode(runSettingsXml);
#pragma warning disable CS0618 // Type or member is obsolete
            return config.TargetFrameworkVersion;
#pragma warning restore CS0618 // Type or member is obsolete
        }

        /// <summary>
        /// Tries to embed the Fakes data collector settings for the given run settings.
        /// </summary>
        /// <param name="runSettings">runsettings</param>
        /// <param name="sources">test sources</param>
        /// <returns>true if runSettings was modified; false otherwise.</returns>
        private static bool TryAddFakesDataCollectorSettings(
            XmlDocument runSettings,
            IEnumerable<string> sources,
            FrameworkVersion framework)
        {
            // If user provided fakes settings don't do anything
            if (XmlRunSettingsUtilities.ContainsDataCollector(runSettings.CreateNavigator(), FakesMetadata.DataCollectorUri))
            {
                return false;
            }

            // A new Fakes Congigurator API makes the decision to add the right datacollector uri to the configuration
            // There now exist two data collector URIs to support two different scenarios. The new scenario involves 
            // using the CLRIE profiler, and the old involves using the Intellitrace profiler (which isn't supported in 
            // .NET Core scenarios). The old API still exists for fallback measures. 

            var newConfigurator = TryGetFakesNewDataCollectorConfigurator();
            if (newConfigurator != null)
            {
                var sourceTFMMap = CreateDictionary(sources, framework);
                var fakesSettings = newConfigurator(sourceTFMMap);
                XmlRunSettingsUtilities.InsertDataCollectorsNode(runSettings.CreateNavigator(), fakesSettings);
                return true;
            }

            return AddFallbackFakesSettings(runSettings, sources, framework);
        }

        private static IDictionary<string, FrameworkVersion> CreateDictionary(IEnumerable<string> sources, FrameworkVersion framework)
        {
            var dict = new Dictionary<string, FrameworkVersion>();
            foreach(var source in sources)
            {
                if (!dict.ContainsKey(source))
                {
                    dict.Add(source, framework);
                }
            }

            return dict;
        }

        private static bool AddFallbackFakesSettings(
            XmlDocument runSettings,
            IEnumerable<string> sources,
            FrameworkVersion framework)
        {

            // The fallback settings is for the old implementation of fakes 
            // that only supports .Net Framework versions
            if (framework != FrameworkVersion.Framework35 &&
                framework != FrameworkVersion.Framework40 &&
                framework != FrameworkVersion.Framework45)
            {
                return false;
            }

            Func<IEnumerable<string>, string> oldConfigurator = TryGetFakesDataCollectorConfigurator();
            if (oldConfigurator == null)
            {
                return false;
            }

            // if no fakes, return settings unchanged
            var fakesConfiguration = oldConfigurator(sources);
            if (fakesConfiguration == null)
            {
                return false;
            }

            // integrate fakes settings in configuration
            // if the settings don't have any data collector settings, populate with empty settings
            EnsureSettingsNode(runSettings, new DataCollectionRunSettings());

            // embed fakes settings
            var fakesSettings = CreateFakesDataCollectorSettings();
            var doc = new XmlDocument();
            using (var xmlReader = XmlReader.Create(
                new StringReader(fakesConfiguration),
                new XmlReaderSettings() { CloseInput = true }))
            {
                doc.Load(xmlReader);
            }

            fakesSettings.Configuration = doc.DocumentElement;
            XmlRunSettingsUtilities.InsertDataCollectorsNode(runSettings.CreateNavigator(), fakesSettings);

            return true;
        }

        /// <summary>
        /// Ensures that an xml element corresponding to the test run settings exists in the setting document.
        /// </summary>
        /// <param name="settings">settings</param>
        /// <param name="settingsNode">settingsNode</param>
        private static void EnsureSettingsNode(XmlDocument settings, TestRunSettings settingsNode)
        {
            Debug.Assert(settingsNode != null, "Invalid Settings Node");
            Debug.Assert(settings != null, "Invalid Settings");

            var root = settings.DocumentElement;
            if (root[settingsNode.Name] == null)
            {
                var newElement = settingsNode.ToXml();
                XmlNode newNode = settings.ImportNode(newElement, true);
                root.AppendChild(newNode);
            }
        }

        private static Func<IEnumerable<string>, string> TryGetFakesDataCollectorConfigurator()
        {
#if NET451
            try
            {
                Assembly assembly = Assembly.Load(FakesConfiguratorAssembly);
                var type = assembly?.GetType(ConfiguratorAssemblyQualifiedName, false);
                var method = type?.GetMethod(ConfiguratorMethodName, new Type[] { typeof(IEnumerable<string>) });
                if (method != null)
                {
                    return (Func<IEnumerable<string>, string>)method.CreateDelegate(typeof(Func<IEnumerable<string>, string>));
                }
            }
            catch (Exception ex)
            {
                if (EqtTrace.IsInfoEnabled)
                {
                    EqtTrace.Info("Failed to create Fakes Configurator. Reason:{0} ", ex);
                }
            }
#endif
            return null;
        }

        private static Func<IDictionary<string, FrameworkVersion>, DataCollectorSettings> TryGetFakesNewDataCollectorConfigurator()
        {
            try
            {
                Assembly assembly = Assembly.Load(FakesConfiguratorAssembly);
                var type = assembly?.GetType(ConfiguratorAssemblyQualifiedName, false);
                var method = type?.GetMethod(ConfiguratorMethodName, new Type[] { typeof(IDictionary<string, FrameworkVersion>) });
                if (method != null)
                {
                    return (Func<IDictionary<string, FrameworkVersion>, DataCollectorSettings>)method.CreateDelegate(typeof(Func<IDictionary<string, FrameworkVersion>, DataCollectorSettings>));
                }
            }
            catch (Exception ex)
            {
                if (EqtTrace.IsInfoEnabled)
                {
                    EqtTrace.Info("Failed to create newly implemented Fakes Configurator. Reason:{0} ", ex);
                }
            }

            return null;
        }

        /// <summary>
        /// Adds the Fakes data collector settings in the run settings document.
        /// </summary>
        /// <returns>
        /// The <see cref="DataCollectorSettings"/>.
        /// </returns>
        private static DataCollectorSettings CreateFakesDataCollectorSettings()
        {
            // embed the fakes run settings
            var settings = new DataCollectorSettings
            {
                AssemblyQualifiedName = FakesMetadata.DataCollectorAssemblyQualifiedName,
                FriendlyName = FakesMetadata.FriendlyName,
                IsEnabled = true,
                Uri = new Uri(FakesMetadata.DataCollectorUri)
            };
            return settings;
        }

        internal static class FakesMetadata
        {
            /// <summary>
            /// Friendly name of the data collector
            /// </summary>
            public const string FriendlyName = "UnitTestIsolation";

            /// <summary>
            /// Gets the URI of the data collector
            /// </summary>
            public const string DataCollectorUri = "datacollector://microsoft/unittestisolation/1.0";

            /// <summary>
            /// Gets the assembly qualified name of the data collector type
            /// </summary>
            public const string DataCollectorAssemblyQualifiedName = "Microsoft.VisualStudio.TraceCollector.UnitTestIsolationDataCollector, Microsoft.VisualStudio.TraceCollector, Version=16.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a";
        }
    }
}