using System;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using System.IO;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Linq;
using System.Text;

namespace Mono.Build.Tasks
{
    public class FilterDeniedAssemblies : Task
    {
        static string s_deniedListFilename = "deniedAssembliesList.txt";

        // Using the valueFactory overload to get exception caching
        static Lazy<ExclusionDB> s_db = new Lazy<ExclusionDB>(() => new ExclusionDB(s_deniedListFilename));

        static bool s_haveWarnedAboutMissingList = false;

        public override bool Execute ()
        {
            try {
                if (s_db.Value.Empty) {
                    // nothing to filter!
                    FilteredReferences = References;

                    return !Log.HasLoggedErrors;
                }
            } catch (FileNotFoundException fe) when (fe.FileName != null && String.Compare(Path.GetFileName(fe.FileName), s_deniedListFilename) == 0) {
                // If the exception is about the missing denied list, then just warn.
                // Any other errors can surface
                if (!s_haveWarnedAboutMissingList) {
                    Log.LogWarning(null, "MSB3911", null, null, -1, -1, -1, -1, $"INTERNAL WARNING: {fe.Message}. Please file a bug report at http://bugzilla.xamarin.com .");
                    Log.LogMessage(MessageImportance.Low, fe.ToString());

                    s_haveWarnedAboutMissingList = true;
                }

                return !Log.HasLoggedErrors;
            }

            var deniedReferencesNotFixedItemsList = new List<ITaskItem> ();
            var filteredItems = new List<ITaskItem> ();

            foreach (var referenceItem in References) {
                // Try to find the path corresponding to a reference
                // - item Include might itself be a path
                // - or it might have a HintPath with the path
                bool foundInHintPath = false;
                var assemblyPathFromReference = referenceItem.GetMetadata("FullPath");

                if (!File.Exists (assemblyPathFromReference)) {
                    var hintPath = referenceItem.GetMetadata ("HintPath");
                    if (!String.IsNullOrEmpty (hintPath)) {
                        assemblyPathFromReference = Path.GetFullPath (hintPath);
                        if (!File.Exists(assemblyPathFromReference))
                            assemblyPathFromReference = null;
                        else
                            foundInHintPath = true;
                    }
                }

                if (assemblyPathFromReference != null && s_db.Value.IsDeniedAssembly (assemblyPathFromReference)) {
                    referenceItem.SetMetadata ("DeniedAssemblyPath", assemblyPathFromReference);

                    // Try to find the "safe" assembly under @SearchPaths, and update the reference

                    var assemblyFilename = Path.GetFileName (assemblyPathFromReference);
                    var safeAssemblyFilePath = SearchPaths
                                                .Select (d => Path.Combine (d, assemblyFilename))
                                                .Where (f => File.Exists (f))
                                                .FirstOrDefault ();

                    if (safeAssemblyFilePath != null) {
                        if (foundInHintPath)
                            referenceItem.SetMetadata ("HintPath", safeAssemblyFilePath);
                        else
                            referenceItem.ItemSpec = safeAssemblyFilePath;

                        Log.LogMessage (MessageImportance.Low, $"Changed the denied assembly reference path from {assemblyPathFromReference} to the safe assembly path {safeAssemblyFilePath}.");
                    } else {
                        // Warn and don't touch the reference
                        Log.LogWarning(null, "MSB3912", null, null, -1, -1, -1, -1,
                                        $"INTERNAL WARNING: Could not find the replacement assembly ({assemblyFilename}) for the denied reference {assemblyPathFromReference}" +
                                        $" in the search paths {StringifyList(SearchPaths)}. This might cause issues at runtime." + 
                                        " Please file a bug report at http://bugzilla.xamarin.com .");

                        deniedReferencesNotFixedItemsList.Add (referenceItem);
                    }
                }

                filteredItems.Add (referenceItem);
            }

            DeniedReferencesThatCouldNotBeFixed = deniedReferencesNotFixedItemsList.ToArray ();
            FilteredReferences = filteredItems.ToArray ();

            return !Log.HasLoggedErrors;
        }

        /// <summary>
        /// Stringify a list of strings, like {"abc, "def", "foo"} to "abc, def and foo"
        /// or {"abc"} to "abc"
        /// <param name="strings">List of strings to stringify</param>
        /// <returns>Stringified list</returns>
        /// </summary>
        static string StringifyList(string[] strings)
        {
            if (strings?.Length == 0)
                return String.Empty;

            var commaSeparatedString = String.Join(", ", strings);
            if (strings.Length > 1)
            {
                commaSeparatedString = commaSeparatedString.Insert(commaSeparatedString.IndexOf(",") + 1, " and");
            }

            return commaSeparatedString;
        }

        [Required]
        public ITaskItem[]  References { get; set; }

        [Required]
        public string[]     SearchPaths { get; set; }

        [Output]
        public ITaskItem[]  DeniedReferencesThatCouldNotBeFixed { get; set; }

        [Output]
        public ITaskItem[]  FilteredReferences { get; set; }
    }

    class ExclusionDB
    {
        public HashSet<string>  ExclusionSet;
        public List<string>     ExclusionNamesList;
        public bool             Empty;

        public ExclusionDB(string deniedListFilename)
        {
            Empty = true;
            ExclusionSet = new HashSet<string>();
            ExclusionNamesList = new List<string>();

            var thisAssembly = typeof (FilterDeniedAssemblies).Assembly;
            var thisPath = thisAssembly.Location;
            var deniedListFilePath = Path.Combine(Path.GetDirectoryName(thisPath), deniedListFilename);
            if (!File.Exists (deniedListFilePath))
                throw new FileNotFoundException($"Denied assembly list not found: {deniedListFilePath}", deniedListFilePath);

            var lines = File.ReadAllLines(deniedListFilePath);
            foreach (var line in lines) {
                var comma = line.IndexOf (",");
                if (comma < 0)
                    continue;

                var filename = line.Substring (0, comma).Trim ();
                if (filename.Length > 0) {
                    ExclusionSet.Add (line);
                    ExclusionNamesList.Add (filename);
                }
            }

            Empty = ExclusionNamesList.Count == 0;
        }

        public bool IsDeniedAssembly (string assemblyFullPath)
        {
            var assemblyFilename = Path.GetFileName (assemblyFullPath);

            return ExclusionNamesList.Contains (assemblyFilename) &&
                    ExclusionSet.Contains (CreateKeyForAssembly (assemblyFullPath));
        }

        static string CreateKeyForAssembly (string fullpath)
        {
            if (String.IsNullOrEmpty (fullpath) || !File.Exists (fullpath))
                return String.Empty;

            var filename = Path.GetFileName (fullpath);
            Version ver;

            using (var stream = File.OpenRead(fullpath))
            using (var peFile = new PEReader(stream))
            {
                var metadataReader = peFile.GetMetadataReader();

                var entry = metadataReader.GetAssemblyDefinition();
                ver = entry.Version;
                var guid = metadataReader.GetGuid(metadataReader.GetModuleDefinition().Mvid);

                var id = guid.ToString (null, CultureInfo.InvariantCulture).ToUpperInvariant ();
                return $"{filename},{id},{ver.Major},{ver.Minor},{ver.Build},{ver.Revision}";
            }
        }
    }
}
