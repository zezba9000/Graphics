using System.Collections.Generic;

namespace UnityEditor.ShaderGraph.Defs
{
    internal interface IStandardNode
    {
        static string Name { get; }
        static int Version { get; }
        static NodeDescriptor NodeDescriptor { get; }
        static NodeUIDescriptor NodeUIDescriptor { get; }
        static FunctionDescriptor FunctionDescriptor { get; }
        static Dictionary<string, string> UIStrings { get; }
        static Dictionary<string, float> UIHints { get; }
    }
}
