using ImmersiveVoice;
using System.Reflection;
using System.Linq.Expressions;

public interface TestDialogueSerializable
{
    void Serialize(TestDialogueWriter writer);
    void Deserialize(TestDialogueReader reader);
}
public sealed class TestDialogueWriter
{
    public readonly List<int> Values = new();
    public void Put(int value) => Values.Add(value);
}
public sealed class TestDialogueReader
{
    public int[] Values;
    private int _index;
    public int GetInt() => Values[_index++];
}

internal static class DialoguePacketScenarios
{
    public static void Run(Action<bool, string> check)
    {
        Type type = DialoguePacketFactory.Create(typeof(TestDialogueSerializable), typeof(TestDialogueWriter), typeof(TestDialogueReader));
        var original = (DialoguePacketData)Activator.CreateInstance(type)!;
        Fill(original);
        var writer = new TestDialogueWriter();
        ((TestDialogueSerializable)original).Serialize(writer);
        var copy = (DialoguePacketData)Activator.CreateInstance(type)!;
        ((TestDialogueSerializable)copy).Deserialize(new TestDialogueReader { Values = writer.Values.ToArray() });
        check(writer.Values.Count == 9 && Equal(original, copy), "optional runtime packet round-trips exactly nine integers");
        check(type.FullName == "ImmersiveVoice.DialoguePacketV1", "packet identity is stable across clients and builds");
        check(typeof(DialoguePacketData).GetFields().All(f => f.FieldType == typeof(int)), "metadata packet contains no names, persistent profile IDs, positions or audio");
    }
    private static void Fill(DialoguePacketData data)
    {
        data.Protocol = 1; data.Kind = 1; data.Caller = 2; data.Actor = 2; data.Epoch = 937;
        data.Sequence = 42; data.Phrase = 31; data.Clip = 7; data.DurationMs = 1234;
    }
    private static bool Equal(DialoguePacketData a, DialoguePacketData b)
        => typeof(DialoguePacketData).GetFields().All(f => Equals(f.GetValue(a), f.GetValue(b)));

#if !PORTABLE_TESTS
    public static ushort Installed(Action<bool, string> check, string root)
    {
        // Only Fika's pure serializer is executed; no Unity object or raid is created.
        Assembly assembly = Assembly.LoadFrom(Path.Combine(root, "BepInEx/plugins/Fika/Fika.Core.dll"));
        Type T(string name) => assembly.GetType("Fika.Core.Networking.LiteNetLib.Utils." + name, true)!;
        Type packet = DialoguePacketFactory.Create(T("INetSerializable"), T("NetDataWriter"), T("NetDataReader"));
        var data = (DialoguePacketData)Activator.CreateInstance(packet)!; Fill(data);
        object writer = Activator.CreateInstance(T("NetDataWriter"))!;
        object processor = Activator.CreateInstance(T("NetPacketProcessor"))!;
        T("NetPacketProcessor").GetMethod("WriteNetSerializable")!.MakeGenericMethod(packet).Invoke(processor, new[] { writer, data });
        byte[] bytes = (byte[])T("NetDataWriter").GetMethod("CopyData")!.Invoke(writer, null)!;
        check(bytes.Length == 38, "installed Fika serializes dialogue into a 38-byte payload including hash");
        DialoguePacketData received = null;
        Action<DialoguePacketData> capture = p => received = p;
        var value = Expression.Parameter(packet);
        Delegate callback = Expression.Lambda(typeof(Action<>).MakeGenericType(packet),
            Expression.Invoke(Expression.Constant(capture), Expression.Convert(value, typeof(DialoguePacketData))), value).Compile();
        T("NetPacketProcessor").GetMethods().Single(m => m.Name == "SubscribeNetSerializable" && m.GetGenericArguments().Length == 1
            && m.GetParameters().Length == 1).MakeGenericMethod(packet).Invoke(processor, new object[] { callback });
        object reader = Activator.CreateInstance(T("NetDataReader"), new object[] { bytes })!;
        T("NetPacketProcessor").GetMethod("ReadAllPackets", new[] { T("NetDataReader") })!.Invoke(processor, new[] { reader });
        check(received != null && Equal(data, received), "installed Fika packet dispatcher reaches the exact runtime packet callback");
        check((bool)T("NetPacketProcessor").GetMethod("RemoveSubscription")!.MakeGenericMethod(packet).Invoke(processor, null)!,
            "installed Fika permits removing the subscription at network-manager teardown");
        return BitConverter.ToUInt16(bytes, 0);
    }
#endif
}
