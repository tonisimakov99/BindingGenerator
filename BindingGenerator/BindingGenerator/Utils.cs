using CppSharp.AST;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BindingGenerator
{
    public static class Utils
    {
        public static ReadOnlyDictionary<PrimitiveType, string> PrimitiveTypesToCsTypesMap = new ReadOnlyDictionary<PrimitiveType, string>(
            new Dictionary<PrimitiveType, string>()
            {
                { PrimitiveType.Int,"int"},
                { PrimitiveType.Long,"int"},
                { PrimitiveType.Float,"float"},
                { PrimitiveType.Double,"double"},
                { PrimitiveType.Bool,"bool"},
                { PrimitiveType.String,"string"},
                { PrimitiveType.ULong,"uint" },
                { PrimitiveType.UInt,"uint" },
                { PrimitiveType.Void,"void" },
                { PrimitiveType.UChar,"byte" },
                { PrimitiveType.UShort,"ushort" },
                { PrimitiveType.Short,"short" },
                { PrimitiveType.SChar,"sbyte" },
                { PrimitiveType.Char,"byte" },
                { PrimitiveType.ULongLong,"ulong" },
                { PrimitiveType.LongLong,"long" }
            }
            );

    }
}
