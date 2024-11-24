using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BindingGenerator
{
    public class RuntimeData
    {
        public required Dictionary<Platform, string> PerPlatformPathes { get; set; }

        public Platform? DefaultPlatform { get; set; }
    }
}
