using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace SystemAutorun.Core.Configuration;

internal static class ConfigurationYamlGuard
{
    internal static void Validate(string yaml, CancellationToken cancellationToken)
    {
        var parser = new Parser(new StringReader(yaml));
        var events = 0; var depth = 0; var aliases = 0; var documents = 0;
        while (parser.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++events > 32768) { throw new ConfigurationException("YAML exceeds the 32768-event configuration limit."); }
            switch (parser.Current)
            {
                case MappingStart or SequenceStart:
                    if (++depth > 64) { throw new ConfigurationException("YAML nesting exceeds 64 levels."); }
                    break;
                case MappingEnd or SequenceEnd: depth--; break;
                case AnchorAlias:
                    if (++aliases > 128) { throw new ConfigurationException("YAML contains more than 128 alias references."); }
                    break;
                case DocumentStart:
                    if (++documents > 1) { throw new ConfigurationException("A configuration must contain exactly one YAML document."); }
                    break;
            }
        }
        if (documents != 1) { throw new ConfigurationException("A configuration must contain exactly one YAML document."); }
    }
}
