using JsonCall.Core;

namespace JsonCall.Specs.Support;

public enum Color
{
    Red,
    Green
}

public sealed record Node(string Name, List<Node> Children);

/// <summary>Basic maths service.</summary>
[JsonRpcContract(Title = "Math API", Version = "1.2.0")]
public interface IMathApi
{
    /// <summary>Adds two numbers.</summary>
    /// <param name="a">First addend.</param>
    /// <param name="b">Second addend.</param>
    Task<int> AddAsync(int a, int b);

    Task<string> GreetAsync(string name, string greeting = "Hello");

    [Obsolete("Use AddAsync")]
    Task<int> PlusAsync(int a, int b);

    [JsonRpcNotification]
    Task LogAsync(string message);

    Task<Person> GetPersonAsync(int id);

    Task<IReadOnlyList<Person>> ListPeopleAsync(Person? filter = null);
}

public interface IRenamedApi
{
    [JsonRpcMethod("math.add")]
    Task<int> AddAsync(int a, int b);
}

[JsonRpcContract(ParamStyle = JsonRpcParamStyle.ByName)]
public interface IByNameApi
{
    Task<int> AddAsync(int a, int b);
}

[JsonRpcContract(ParamStyle = JsonRpcParamStyle.ByPosition)]
public interface IByPositionApi
{
    Task<int> AddAsync(int a, int b);
}

[JsonRpcContract(ParamStyle = JsonRpcParamStyle.Either)]
public interface IEitherApi
{
    Task<int> AddAsync(int a, int b);
}

public interface INodeApi
{
    Task<Node> GetNodeAsync();
}

public interface IErrorsApi
{
    [JsonRpcError(404, "Person not found")]
    Task<Person> GetPersonAsync(int id);
}

public interface IDuplicateErrorsApi
{
    [JsonRpcError(404, "Person not found")]
    [JsonRpcError(404, "Not found")]
    Task<Person> GetPersonAsync(int id);
}

public interface IReservedErrorApi
{
    [JsonRpcError(-32010, "Server error")]
    Task<Person> GetPersonAsync(int id);
}

[JsonRpcTag("math")]
public interface ITaggedApi
{
    Task<int> AddAsync(int a, int b);

    Task<int> NegateAsync(int a);
}

public interface ITypeZoo
{
    [JsonRpcMethod("DateTimeOffset")]
    Task<int> DateTimeOffsetAsync(DateTimeOffset value);

    [JsonRpcMethod("TimeSpan")]
    Task<int> TimeSpanAsync(TimeSpan value);

    [JsonRpcMethod("enum Color")]
    Task<int> ColorAsync(Color value);

    [JsonRpcMethod("int?")]
    Task<int> NullableIntAsync(int? value);

    [JsonRpcMethod("byte[]")]
    Task<int> BytesAsync(byte[] value);

    [JsonRpcMethod("Dictionary<string,int>")]
    Task<int> DictionaryAsync(Dictionary<string, int> value);

    [JsonRpcMethod("Guid")]
    Task<int> GuidTypeAsync(Guid value);
}
