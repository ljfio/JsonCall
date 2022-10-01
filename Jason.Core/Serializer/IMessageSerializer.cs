using Jason.Core.Model;

namespace Jason.Core;

public interface IMessageSerializer
{
    Task SerializeAsync<T>(Stream stream, T value, CancellationToken cancellationToken = default);
    Task<T> DeserializeAsync<T>(Stream stream, CancellationToken cancellationToken = default);
}