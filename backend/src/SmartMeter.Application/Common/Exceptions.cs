namespace SmartMeter.Application.Common;

public sealed class NotFoundException(string resource, string key)
    : Exception($"{resource} '{key}' was not found.")
{
    public string Resource { get; } = resource;
    public string Key { get; } = key;
}

public sealed class BadRequestException(string message) : Exception(message);

public sealed class ConflictException(string message) : Exception(message);
