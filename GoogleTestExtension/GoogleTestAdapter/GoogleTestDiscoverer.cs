﻿using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using GoogleTestAdapter.Dia;
using GoogleTestAdapter.Helpers;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Adapter;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;

namespace GoogleTestAdapter
{
    [DefaultExecutorUri(GoogleTestExecutor.ExecutorUriString)]
    [FileExtension(".exe")]
    public class GoogleTestDiscoverer : ITestDiscoverer
    {
        class SuiteCasePair
        {
            public string TestSuite;
            public string TestCase;
        }

        public class SourceFileLocation
        {
            public string Symbol;
            public string Sourcefile;
            public uint Line;
            public List<Trait> Traits;
        }

        public const string TestFinderRegex = @"[Tt]est[s]?\.exe";

        private static readonly Regex CompiledTestFinderRegex = new Regex(TestFinderRegex, RegexOptions.Compiled);

        private TestEnvironment TestEnvironment { get; set; }

        public GoogleTestDiscoverer() : this(null) { }

        internal GoogleTestDiscoverer(TestEnvironment testEnvironment)
        {
            this.TestEnvironment = testEnvironment;
        }

        public void DiscoverTests(IEnumerable<string> executables, IDiscoveryContext discoveryContext,
            IMessageLogger logger, ITestCaseDiscoverySink discoverySink)
        {
            if (TestEnvironment == null)
            {
                TestEnvironment = new TestEnvironment(new Options(), logger);
            }

            TestEnvironment.CheckDebugModeForDiscoverageCode();

            List<string> googleTestExecutables = GetAllGoogleTestExecutables(executables);
            VsTestFrameworkReporter reporter = new VsTestFrameworkReporter(TestEnvironment);
            foreach (string executable in googleTestExecutables)
            {
                List<TestCase> testCases = GetTestsFromExecutable(executable);
                reporter.ReportTestsFound(discoverySink, testCases);
            }
        }

        internal List<TestCase> GetTestsFromExecutable(string executable)
        {
            List<string> consoleOutput = new ProcessLauncher(TestEnvironment).GetOutputOfCommand("", executable, GoogleTestConstants.ListTestsOption, false, false, null, null);
            List<SuiteCasePair> suiteCasePairs = ParseTestCases(consoleOutput);
            suiteCasePairs.Reverse();
            List<SourceFileLocation> sourceFileLocations = GetSourceFileLocations(executable, suiteCasePairs);

            TestEnvironment.LogInfo("GTA: Found " + suiteCasePairs.Count + " tests in executable " + executable);

            List<TestCase> testCases = new List<TestCase>();
            foreach (SuiteCasePair suiteCasePair in suiteCasePairs)
            {
                testCases.Add(ToTestCase(executable, suiteCasePair, sourceFileLocations));
                TestEnvironment.LogInfo("GTA: Added testcase " + suiteCasePair.TestSuite + "." + suiteCasePair.TestCase, TestEnvironment.LogType.Debug);
            }
            return testCases;
        }

        internal bool IsGoogleTestExecutable(string executable, string customRegex = "")
        {
            bool matches;
            string regexUsed;
            if (string.IsNullOrWhiteSpace(customRegex))
            {
                regexUsed = TestFinderRegex;
                matches = CompiledTestFinderRegex.IsMatch(executable);
            }
            else
            {
                regexUsed = customRegex;
                try
                {
                    matches = Regex.IsMatch(executable, customRegex);
                }
                catch (ArgumentException e)
                {
                    TestEnvironment.LogError(
                        "GTA: Regex '" + regexUsed + "' configured under Options/Google Test Adapter can not be parsed: " + e.Message);
                    matches = false;
                }
                catch (RegexMatchTimeoutException e)
                {
                    TestEnvironment.LogError(
                        "GTA: Regex '" + regexUsed + "' configured under Options/Google Test Adapter timed out: " + e.Message);
                    matches = false;
                }
            }

            TestEnvironment.LogInfo(
                    "GTA: " + executable + (matches ? " matches " : " does not match ") + "regex '" + regexUsed + "'", TestEnvironment.LogType.UserDebug);

            return matches;
        }

        private List<SuiteCasePair> ParseTestCases(List<string> output)
        {
            List<SuiteCasePair> suiteCasePairs = new List<SuiteCasePair>();
            string currentSuite = "";
            foreach (string line in output)
            {
                string trimmedLine = line.Trim('.', '\n', '\r');
                if (trimmedLine.StartsWith("  "))
                {
                    suiteCasePairs.Add(new SuiteCasePair
                    {
                        TestSuite = currentSuite,
                        TestCase = trimmedLine.Substring(2)
                    });
                }
                else
                {
                    string[] split = trimmedLine.Split(new[] { GoogleTestConstants.ParameterValueMarker }, StringSplitOptions.RemoveEmptyEntries);
                    currentSuite = split.Length > 0 ? split[0] : trimmedLine;
                }
            }

            return suiteCasePairs;
        }

        private List<SourceFileLocation> GetSourceFileLocations(string executable, List<SuiteCasePair> testcases)
        {
            List<string> symbols = testcases.Select(GetGoogleTestCombinedName).ToList();
            string SymbolFilterString = "*" + GoogleTestConstants.TestBodySignature;
            DiaResolver resolver = new DiaResolver(TestEnvironment);
            return resolver.ResolveAllMethods(executable, symbols, SymbolFilterString);
        }

        private string GetGoogleTestCombinedName(SuiteCasePair pair)
        {
            if (!pair.TestCase.Contains(GoogleTestConstants.ParameterizedTestMarker))
            {
                return GoogleTestConstants.GetTestMethodSignature(pair.TestSuite, pair.TestCase);
            }

            int index = pair.TestSuite.IndexOf('/');
            string suite = index < 0 ? pair.TestSuite : pair.TestSuite.Substring(index + 1);

            index = pair.TestCase.IndexOf('/');
            string testName = index < 0 ? pair.TestCase : pair.TestCase.Substring(0, index);

            return GoogleTestConstants.GetTestMethodSignature(suite, testName);
        }

        private TestCase ToTestCase(string executable, SuiteCasePair suiteCasePair, List<SourceFileLocation> sourceFileLocations)
        {
            string displayName = suiteCasePair.TestSuite + "." + suiteCasePair.TestCase;
            string symbolName = GetGoogleTestCombinedName(suiteCasePair);

            foreach (SourceFileLocation location in sourceFileLocations)
            {
                if (location.Symbol.Contains(symbolName))
                {
                    TestCase testCase = new TestCase(displayName, new Uri(GoogleTestExecutor.ExecutorUriString), executable)
                    {
                        DisplayName = displayName,
                        CodeFilePath = location.Sourcefile,
                        LineNumber = (int)location.Line
                    };
                    testCase.Traits.AddRange(GetTraits(testCase.FullyQualifiedName, location.Traits));
                    return testCase;
                }
            }

            TestEnvironment.LogWarning("GTA: Could not find source location for test " + displayName);
            return new TestCase(displayName, new Uri(GoogleTestExecutor.ExecutorUriString), executable)
            {
                DisplayName = displayName
            };
        }

        private IEnumerable<Trait> GetTraits(string fullyQualifiedName, List<Trait> traits)
        {
            foreach (RegexTraitPair pair in TestEnvironment.Options.TraitsRegexesBefore.Where(p => Regex.IsMatch(fullyQualifiedName, p.Regex)))
            {
                if (!traits.Exists(T => T.Name == pair.Trait.Name))
                {
                    traits.Add(pair.Trait);
                }
            }

            foreach (RegexTraitPair pair in TestEnvironment.Options.TraitsRegexesAfter.Where(p => Regex.IsMatch(fullyQualifiedName, p.Regex)))
            {
                bool replacedTrait = false;
                foreach (Trait traitToModify in traits.ToArray().Where(T => T.Name == pair.Trait.Name))
                {
                    replacedTrait = true;
                    traits.Remove(traitToModify);
                    if (!traits.Contains(pair.Trait))
                    {
                        traits.Add(pair.Trait);
                    }
                }
                if (!replacedTrait)
                {
                    traits.Add(pair.Trait);
                }
            }
            return traits;
        }

        private List<string> GetAllGoogleTestExecutables(IEnumerable<string> allExecutables)
        {
            return allExecutables.Where(e => IsGoogleTestExecutable(e, TestEnvironment.Options.TestDiscoveryRegex)).ToList();
        }

    }

}
