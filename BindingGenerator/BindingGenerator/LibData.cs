using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BindingGenerator
{
    public class LibData
    {
        public required string LibName { get; set; }
        public required string FuncsHeaderPath { get; set; }
        public required RuntimeData RuntimeData { get; set; }
    }
}
