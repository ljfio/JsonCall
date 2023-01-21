using System.Buffers.Text;
using Jason.Core.Model;

namespace Jason.Core.Tests.Unit;

public class DefaultMessageSerializerTests
{
    [Fact]
    public async Task SerializeJsonRpcParamsAsync()
    {
        // Arrange
        var serializer = new DefaultMessageSerializer();
        var values = new object[] { "hello", 1 };
        var parameters = new JsonRpcParams(values);
        
        using var stream = new MemoryStream();
        
        // Act
        await serializer.SerializeAsync(stream, parameters);

        stream.Seek(0, SeekOrigin.Begin);
        
        // Assert
        using var reader= new StreamReader(stream);

        string result = await reader.ReadToEndAsync();
        
        Assert.Equal("[\"hello\",1]", result);
    }
}