using System;
using System.Reflection;
using System.Reflection.Emit;

namespace ImmersiveVoice
{
    // Public only so the optional runtime packet can inherit it. Fixed-size
    // integers, no player names, positions, paths or audio on this channel.
    public class DialoguePacketData
    {
        public int Protocol, Kind, Caller, Actor, Epoch, Sequence, Phrase, Clip, DurationMs;
    }

    internal static class DialoguePacketFactory
    {
        public static Type Create(Type contract, Type writer, Type reader)
        {
            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("ImmersiveVoice.DialogueProtocol1"), AssemblyBuilderAccess.Run);
            var type = assembly.DefineDynamicModule("Packets").DefineType("ImmersiveVoice.DialoguePacketV1",
                TypeAttributes.Public | TypeAttributes.Sealed, typeof(DialoguePacketData), new[] { contract });
            type.DefineDefaultConstructor(MethodAttributes.Public);
            string[] fields = { "Protocol", "Kind", "Caller", "Actor", "Epoch", "Sequence", "Phrase", "Clip", "DurationMs" };
            foreach (bool writing in new[] { true, false })
            {
                string name = writing ? "Serialize" : "Deserialize";
                var method = type.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final,
                    typeof(void), new[] { writing ? writer : reader });
                ILGenerator il = method.GetILGenerator();
                MethodInfo io = writing ? writer.GetMethod("Put", new[] { typeof(int) }) : reader.GetMethod("GetInt", System.Type.EmptyTypes);
                if (io == null) throw new MissingMethodException(name);
                foreach (string field in fields)
                {
                    il.Emit(writing ? OpCodes.Ldarg_1 : OpCodes.Ldarg_0);
                    il.Emit(writing ? OpCodes.Ldarg_0 : OpCodes.Ldarg_1);
                    if (writing) il.Emit(OpCodes.Ldfld, typeof(DialoguePacketData).GetField(field));
                    il.Emit(OpCodes.Callvirt, io);
                    if (!writing) il.Emit(OpCodes.Stfld, typeof(DialoguePacketData).GetField(field));
                }
                il.Emit(OpCodes.Ret);
                type.DefineMethodOverride(method, contract.GetMethod(name));
            }
            return type.CreateType();
        }
    }
}
