using System.Collections.Generic;
using Usage = UnityEditor.ShaderGraph.GraphDelta.GraphType.Usage;

namespace UnityEditor.ShaderGraph.Defs
{
    internal class SaturateNode : IStandardNode
    {
        public static string Name = "Saturate";
        public static int Version = 1;

        public static FunctionDescriptor FunctionDescriptor => new(
            Version,
            Name,
            "Out = saturate(In);",
            new ParameterDescriptor("In", TYPE.Vector, Usage.In),
            new ParameterDescriptor("Out", TYPE.Vector, Usage.Out)
        );

        public static NodeUIDescriptor NodeUIDescriptor => new(
            Version,
            Name,
            tooltip: "Clamps the input between 0 and 1",
            categories: new string[2] { "Math", "Range" },
            synonyms: new string[0],
            parameters: new ParameterUIDescriptor[2] {
                new ParameterUIDescriptor(
                    name: "In",
                    tooltip: "input value"
                ),
                new ParameterUIDescriptor(
                    name: "Out",
                    tooltip: "the input clamped between 0 and 1"
                )
            }
        );

        public static Dictionary<string, string> UIStrings => new()
        {
            { "Name.Synonyms", "clamp" },
            { "Tooltip", "Clamps the input between 0 and 1" },
            { "Category", "Math, Range" },
            { "Parameters.In.Tooltip", "input value" },
            { "Parameters.Out.Tooltip", "the input clamped between 0 and 1" }
        };
    }
}
