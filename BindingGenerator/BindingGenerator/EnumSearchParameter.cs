using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BindingGenerator
{
    public class EnumSearchParameter
    {
        public EnumSearchParameter(string prefix, string excludePrefix)
        {
            Prefix = prefix;
            ExcludePrefix = excludePrefix;
        }

        public string Prefix { get; set; }

        public string ExcludePrefix { get; set; }
    }
}
