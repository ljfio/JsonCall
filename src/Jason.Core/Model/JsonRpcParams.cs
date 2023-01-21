using System.Collections;
using System.Text.Json.Serialization;
using Jason.Core.Converters;

namespace Jason.Core.Model;

[JsonConverter(typeof(JsonRpcParamsConverter))]
public class JsonRpcParams : IDictionary<string, object>, ICollection<object>
{
    private readonly IDictionary<string, object> _dictionary;

    private readonly ICollection<object> _collection;

    public JsonRpcParams(JsonRpcParamsType type)
    {
        Type = type;

        if (type == JsonRpcParamsType.ByName)
            _dictionary = new Dictionary<string, object>();
        else if (type == JsonRpcParamsType.ByPosition)
            _collection = new List<object>();
        else
            throw new ArgumentException();
    }

    public JsonRpcParams(IDictionary<string, object> value)
    {
        Type = JsonRpcParamsType.ByName;

        _dictionary = new Dictionary<string, object>(value);
    }

    public JsonRpcParams(IEnumerable<object> value)
    {
        Type = JsonRpcParamsType.ByPosition;

        _collection = new List<object>(value);
    }

    public JsonRpcParamsType Type { get; }

    public ICollection<string> Keys
    {
        get
        {
            if (Type == JsonRpcParamsType.ByPosition)
                throw new InvalidOperationException();

            return _dictionary.Keys;
        }
    }

    public ICollection<object> Values
    {
        get
        {
            if (Type == JsonRpcParamsType.ByPosition)
                throw new InvalidOperationException();

            return _dictionary.Values;
        }
    }

    IEnumerator<object> IEnumerable<object>.GetEnumerator()
    {
        if (Type == JsonRpcParamsType.ByName)
            throw new InvalidOperationException();

        return _collection.GetEnumerator();
    }

    IEnumerator<KeyValuePair<string, object>> IEnumerable<KeyValuePair<string, object>>.GetEnumerator()
    {
        if (Type == JsonRpcParamsType.ByPosition)
            throw new InvalidOperationException();

        return _dictionary.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        if (Type == JsonRpcParamsType.ByName)
            return _dictionary.GetEnumerator();
        else if (Type == JsonRpcParamsType.ByPosition)
            return _dictionary.GetEnumerator();
        else
            throw new InvalidOperationException();
    }

    public void Add(KeyValuePair<string, object> item)
    {
        if (Type == JsonRpcParamsType.ByPosition)
            throw new InvalidOperationException();

        _dictionary.Add(item);
    }

    public void Add(object item)
    {
        if (Type == JsonRpcParamsType.ByName)
            throw new InvalidOperationException();

        _collection.Add(item);
    }

    void ICollection<object>.Clear()
    {
        if (Type == JsonRpcParamsType.ByName)
            throw new InvalidOperationException();

        _collection.Clear();
    }

    public bool Contains(object item)
    {
        if (Type == JsonRpcParamsType.ByName)
            throw new InvalidOperationException();

        return _collection.Contains(item);
    }

    public void CopyTo(object[] array, int arrayIndex)
    {
        if (Type == JsonRpcParamsType.ByName)
            throw new InvalidOperationException();

        _collection.CopyTo(array, arrayIndex);
    }

    public bool Remove(object item)
    {
        if (Type == JsonRpcParamsType.ByName)
            throw new InvalidOperationException();

        return _collection.Remove(item);
    }

    int ICollection<object>.Count
    {
        get
        {
            if (Type == JsonRpcParamsType.ByName)
                throw new InvalidOperationException();

            return _collection.Count;
        }
    }

    bool ICollection<object>.IsReadOnly
    {
        get
        {
            if (Type == JsonRpcParamsType.ByName)
                throw new InvalidOperationException();

            return _collection.IsReadOnly;
        }
    }

    void ICollection<KeyValuePair<string, object>>.Clear()
    {
        if (Type == JsonRpcParamsType.ByPosition)
            throw new InvalidOperationException();

        _dictionary.Clear();
    }

    public bool Contains(KeyValuePair<string, object> item)
    {
        if (Type == JsonRpcParamsType.ByPosition)
            throw new InvalidOperationException();

        return _dictionary.Contains(item);
    }

    public void CopyTo(KeyValuePair<string, object>[] array, int arrayIndex)
    {
        if (Type == JsonRpcParamsType.ByPosition)
            throw new InvalidOperationException();

        _dictionary.CopyTo(array, arrayIndex);
    }

    public bool Remove(KeyValuePair<string, object> item)
    {
        if (Type == JsonRpcParamsType.ByPosition)
            throw new InvalidOperationException();

        return _dictionary.Remove(item);
    }

    int ICollection<KeyValuePair<string, object>>.Count
    {
        get
        {
            if (Type == JsonRpcParamsType.ByPosition)
                throw new InvalidOperationException();

            return _dictionary.Count;
        }
    }

    bool ICollection<KeyValuePair<string, object>>.IsReadOnly
    {
        get
        {
            if (Type == JsonRpcParamsType.ByPosition)
                throw new InvalidOperationException();

            return _dictionary.IsReadOnly;
        }
    }

    public void Add(string key, object value)
    {
        if (Type == JsonRpcParamsType.ByPosition)
            throw new InvalidOperationException();

        _dictionary.Add(key, value);
    }

    public bool ContainsKey(string key)
    {
        if (Type == JsonRpcParamsType.ByPosition)
            throw new InvalidOperationException();

        return _dictionary.ContainsKey(key);
    }

    public bool Remove(string key)
    {
        if (Type == JsonRpcParamsType.ByPosition)
            throw new InvalidOperationException();

        return _dictionary.Remove(key);
    }

    public bool TryGetValue(string key, out object value)
    {
        if (Type == JsonRpcParamsType.ByPosition)
            throw new InvalidOperationException();

        return _dictionary.TryGetValue(key, out value);
    }

    public object this[string key]
    {
        get
        {
            if (Type == JsonRpcParamsType.ByPosition)
                throw new InvalidOperationException();
            
            return _dictionary[key];
        }
        set
        {
            if (Type == JsonRpcParamsType.ByPosition)
                throw new InvalidOperationException();

            _dictionary[key] = value;
        }
    }
}