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
                new[] { $"{Environment.CurrentDirectory}/headers/overrideParameterTest" },
                "log.h",
                "./overrideParameterTest",
                "Lib",
                "Lib",
                "LibApi",
                fieldParametersTypeOverrides: new Dictionary<string, string>()
                {
                    {"prio","android_LogPriority" }
                }
                );

            Assert.True(File.Exists("overrideParameterTest/LibApi.cs"));

            var libApiLines = File.ReadAllLines("overrideParameterTest/LibApi.cs");
            Assert.Contains(libApiLines, t => t.Contains("__android_log_print"));
            Assert.Contains(libApiLines, t => t.Contains("namespace Lib"));
            Assert.Contains(libApiLines, t => t.Contains("android_LogPriority prio"));
            Assert.Contains("using System.Runtime.InteropServices", libApiLines[0]);
        }

        [Fact]
        public void AnonimusEnumsTest()
        {
            Generator.Generate(
               new[] { $"{Environment.CurrentDirectory}/headers/anonimusEnumsTest" },
               "freetype.h",
               "./anonimusEnumsTest",
               "Lib",
               "Lib",
               "LibApi",
               anonymousEnumName: "FT_Error",
               typedefStrategies:new Dictionary<string, TypedefStrategy>()
               {
                   { "FT_Error", TypedefStrategy.AsIs}
               }
             );
            Assert.True(File.Exists("anonimusEnumsTest/LibApi.cs"));
            Assert.True(File.Exists("anonimusEnumsTest/FT_Error.cs"));
            Assert.True(File.Exists("anonimusEnumsTest/SomeEnum.cs"));
        }

        [Fact]
        public void TypeRedefinitionTest()
        {
            Generator.Generate(
               new[] { $"{Environment.CurrentDirectory}/headers/typeRedefinitionTest" },
               "redefinition.h",
               "./typeRedefinitionTest",
               "Lib",
               "Lib",
               "LibApi",
               logger: loggerFactory.CreateLogger<Generator>()
             );

        }
    }
}