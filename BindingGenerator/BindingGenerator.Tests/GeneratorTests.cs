using CppSharp.Types.Std;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.Utilities;
using Serilog;
using Xunit.Abstractions;

namespace BindingGenerator.Tests
{
    public class UnitTest1
    {
        ILoggerFactory loggerFactory;
        public UnitTest1(ITestOutputHelper output)
        {
            Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
               .Enrich.FromLogContext()
               .WriteTo.TestOutput(output)
               .CreateLogger();
            loggerFactory = LoggerFactory.Create((builder) => { builder.AddSerilog(Log.Logger); });
        }

        [Fact]
        public void OverrideParameterTest()
        {
            Generator.Generate(
                [$"{Environment.CurrentDirectory}/headers/overrideParameterTest"],
                [new LibData() { FuncsHeaderPath = "log.h", LibFileImportedNamePath = "lib.so", LibName = "Lib" }],
                "./overrideParameterTest",
                "Lib",
                fieldParametersTypeOverrides: new Dictionary<string, string>()
                {
                    {"prio","android_LogPriority" }
                }
            );

            Assert.True(File.Exists("overrideParameterTest/Lib.cs"));

            var libApiLines = File.ReadAllLines("overrideParameterTest/Lib.cs");
            Assert.Contains(libApiLines, t => t.Contains("__android_log_print"));
            Assert.Contains(libApiLines, t => t.Contains("namespace Lib"));
            Assert.Contains(libApiLines, t => t.Contains("android_LogPriority prio"));
            Assert.Contains("using System.Runtime.InteropServices", libApiLines[0]);
        }

        [Fact]
        public void AnonymousEnumsTest()
        {
            Generator.Generate(
               new[] { $"{Environment.CurrentDirectory}/headers/anonymousEnumsTest" },
                [new LibData() { FuncsHeaderPath = "freetype.h", LibFileImportedNamePath = "lib.so", LibName = "Lib" }],
               "./anonymousEnumsTest",
               "Lib",
               anonymousEnumPrefixes: new List<string>() { "Prefix" },
               typedefStrategies: new Dictionary<string, TypedefStrategy>()
               {
                   { "FT_Error", TypedefStrategy.AsIs}
               },
               fieldParametersTypeOverrides: new Dictionary<string, string>()
               {
                   { "b", "Prefix" }
               },
               notFoundTypesOverrides: new Dictionary<string, string>()
               {
                   { "FT_Error", "Prefix" }
               }
             );
            Assert.True(File.Exists("anonymousEnumsTest/Lib.cs"));
            Assert.True(File.Exists("anonymousEnumsTest/Prefix.cs"));
            Assert.True(File.Exists("anonymousEnumsTest/SomeEnum.cs"));
        }

        [Fact]
        public void TypeRedefinitionTest()
        {
            Generator.Generate(
               new[] { $"{Environment.CurrentDirectory}/headers/typeRedefinitionTest" },
               [new LibData() { FuncsHeaderPath = "redefinition.h", LibFileImportedNamePath = "lib.so", LibName = "Lib" }],
               "./typeRedefinitionTest",
               "Lib",
               logger: loggerFactory.CreateLogger<Generator>()
             );

        }

        [Fact]
        public void MultiHeaderTest()
        {
            Generator.Generate(
               new[] { $"{Environment.CurrentDirectory}/headers/multiheaderFuncTest" },
               [new LibData() { FuncsHeaderPath = "headerB.h", LibFileImportedNamePath = "lib.so", LibName = "LibB" },
                new LibData() { FuncsHeaderPath = "headerC.h", LibFileImportedNamePath = "lib.so", LibName = "LibC" }],
               "./multiheaderFuncTest",
               "Lib",
               logger: loggerFactory.CreateLogger<Generator>()
             );

            Assert.True(File.Exists("multiheaderFuncTest/A.cs"));
            Assert.True(File.Exists("multiheaderFuncTest/B.cs"));
            Assert.True(File.Exists("multiheaderFuncTest/LibB.cs"));
            Assert.True(File.Exists("multiheaderFuncTest/LibC.cs"));


            var libBLines = File.ReadAllLines("multiheaderFuncTest/LibB.cs");
            Assert.Contains(libBLines, t => t.Contains("FuncA"));
            Assert.DoesNotContain(libBLines, t => t.Contains("FuncB"));

            var libCLines = File.ReadAllLines("multiheaderFuncTest/LibC.cs");
            Assert.Contains(libCLines, t => t.Contains("FuncB"));
            Assert.DoesNotContain(libCLines, t => t.Contains("FuncA"));
        }

        [Fact]
        public void AnonymousEnumsPrefixesTest()
        {
            Generator.Generate(
               new[] { $"{Environment.CurrentDirectory}/headers/anonymousEnumsPrefixesTest" },
                [new LibData() { FuncsHeaderPath = "multi_enum_anonymous.h", LibFileImportedNamePath = "lib.so", LibName = "Lib" }],
               "./anonymousEnumsPrefixesTest",
               "Lib",
               anonymousEnumPrefixes: new List<string>() { "SOME", "SOME2" },
               fieldParametersTypeOverrides: new Dictionary<string, string>()
               {
                   { "b","SOME"},
                   { "d","SOME2"}
               }
             );
            Assert.True(File.Exists("anonymousEnumsPrefixesTest/Lib.cs"));
        }
    }
}