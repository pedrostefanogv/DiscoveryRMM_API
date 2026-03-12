using System.Text;
using Meduza.Core.ValueObjects;
using Meduza.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace Meduza.Tests;

/// <summary>
/// Testes E2E para validação da implementação de Object Storage
/// </summary>
public class ObjectStorageTests
{
    private LocalObjectStorageProvider _storage;
    private TestLogger _logger;

    [SetUp]
    public void Setup()
    {
        _logger = new TestLogger();
        _storage = new LocalObjectStorageProvider(_logger);
    }

    [Test]
    public async Task Upload_CreateFile_Success()
    {
        // Arrange
        var objectKey = "test/upload-test.pdf";
        var fileContent = Encoding.UTF8.GetBytes("Test file content");
        using var stream = new MemoryStream(fileContent);
        
        // Act
        var result = await _storage.UploadAsync(objectKey, stream, "application/pdf");
        
        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.ObjectKey, Is.EqualTo(objectKey));
        Assert.That(result.SizeBytes, Is.EqualTo(fileContent.Length));
        Console.WriteLine("✓ Upload test passed");
    }

    [Test]
    public async Task ExistsAsync_ReturnsTrueAfterUpload()
    {
        // Arrange
        var objectKey = "test/exists-test.txt";
        var fileContent = Encoding.UTF8.GetBytes("Test content");
        using var stream = new MemoryStream(fileContent);
        
        // Act
        await _storage.UploadAsync(objectKey, stream, "text/plain");
        var exists = await _storage.ExistsAsync(objectKey);
        
        // Assert
        Assert.That(exists, Is.True);
        Console.WriteLine("✓ Exists test passed");
    }

    [Test]
    public async Task GetMetadataAsync_ReturnsValidMetadata()
    {
        // Arrange
        var objectKey = "test/metadata-test.txt";
        var fileContent = Encoding.UTF8.GetBytes("Content with metadata");
        using var stream = new MemoryStream(fileContent);
        
        // Act
        var uploaded = await _storage.UploadAsync(objectKey, stream, "text/plain");
        var metadata = await _storage.GetMetadataAsync(objectKey);
        
        // Assert
        Assert.That(metadata, Is.Not.Null);
        Assert.That(metadata.ObjectKey, Is.EqualTo(objectKey));
        Assert.That(metadata.SizeBytes, Is.EqualTo(fileContent.Length));
        Console.WriteLine("✓ Metadata test passed");
    }

    [Test]
    public async Task DownloadAsync_ReturnsCorrectContent()
    {
        // Arrange
        var objectKey = "test/download-test.pdf";
        var expectedContent = Encoding.UTF8.GetBytes("Expected file content");
        using (var stream = new MemoryStream(expectedContent))
        {
            await _storage.UploadAsync(objectKey, stream, "application/pdf");
        }
        
        // Act
        using var downloadStream = await _storage.DownloadAsync(objectKey);
        using var reader = new StreamReader(downloadStream);
        var downloadedContent = await reader.ReadToEndAsync();
        
        // Assert
        Assert.That(downloadedContent, Is.EqualTo(Encoding.UTF8.GetString(expectedContent)));
        Console.WriteLine("✓ Download test passed");
    }

    [Test]
    public async Task DeleteAsync_RemovesFile()
    {
        // Arrange
        var objectKey = "test/delete-test.txt";
        var fileContent = Encoding.UTF8.GetBytes("Content to delete");
        using (var stream = new MemoryStream(fileContent))
        {
            await _storage.UploadAsync(objectKey, stream, "text/plain");
        }
        
        // Act
        await _storage.DeleteAsync(objectKey);
        var exists = await _storage.ExistsAsync(objectKey);
        
        // Assert
        Assert.That(exists, Is.False);
        Console.WriteLine("✓ Delete test passed");
    }
}

/// <summary>Mock logger implementation for testing</summary>
public class TestLogger : ILogger<LocalObjectStorageProvider>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
    
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, EventId eventId, TState state, 
        Exception? exception, Func<TState, Exception?, string> formatter) where TState : notnull
    {
        var message = formatter(state, exception);
        Console.WriteLine($"[{logLevel}] {message}");
    }
}
