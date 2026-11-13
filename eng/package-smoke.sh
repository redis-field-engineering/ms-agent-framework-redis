#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
artifact_dir="$(cd "${1:-artifacts/packages}" && pwd)"
version="$(./eng/validate-packages.sh "$artifact_dir")"
smoke_dir="$(mktemp -d)"
trap 'rm -rf "$smoke_dir"' EXIT
python3 - "$artifact_dir" "$smoke_dir" "$version" <<'PY'
import sys
from pathlib import Path
from xml.sax.saxutils import escape
feed, target, version = sys.argv[1:]
p = Path(target)
p.joinpath('NuGet.Config').write_text(f'''<configuration>
  <packageSources><clear /><add key="release" value="{escape(feed, {'"': '&quot;'})}" /><add key="nuget" value="https://api.nuget.org/v3/index.json" /></packageSources>
  <packageSourceMapping>
    <packageSource key="release"><package pattern="Redis.Iris.*" /></packageSource>
    <packageSource key="nuget"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>''')
p.joinpath('Consumer.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>
<ItemGroup><PackageReference Include="Redis.Iris.AgentFramework" Version="[{escape(version)}]" /></ItemGroup></Project>''')
p.joinpath('Program.cs').write_text('''using Redis.Iris.AgentFramework;
using Redis.Iris.AgentMemory;
using Redis.Iris.ContextRetriever;
using Redis.Iris.ContextRetriever.Modeling;
var types = new[] { typeof(RedisIrisAgentMemoryOptions), typeof(RedisAgentMemoryClient), typeof(ContextRetrieverClientCache), typeof(ContextDataModel) };
foreach (var type in types) Console.WriteLine(type.FullName);
''')
PY
# A new global packages directory prevents preinstalled connector dependencies from hiding omissions.
dotnet restore "$smoke_dir/Consumer.csproj" --configfile "$smoke_dir/NuGet.Config" --packages "$smoke_dir/packages"
dotnet run --project "$smoke_dir/Consumer.csproj" -c Release --no-restore
# Verify runtime isolation before explicitly opting into administrative APIs.
python3 - "$smoke_dir" "$version" <<'PY'
import json
import sys
from pathlib import Path
root = Path(sys.argv[1])
assets = json.loads((root / 'obj/project.assets.json').read_text())
assert not any(name.startswith(('Redis.Iris.ContextRetriever.Management/', 'Redis.Iris.AgentMemory.Management/')) for name in assets['libraries'])
project = root / 'Consumer.csproj'
project.write_text(project.read_text().replace('</ItemGroup>',
    f'<PackageReference Include="Redis.Iris.ContextRetriever.Management" Version="[{sys.argv[2]}]" /><PackageReference Include="Redis.Iris.AgentMemory.Management" Version="[{sys.argv[2]}]" /></ItemGroup>'))
with (root / 'Program.cs').open('a') as output:
    output.write('\nConsole.WriteLine(typeof(Redis.Iris.ContextRetriever.Management.ContextRetrieverManagementClient).FullName);\nConsole.WriteLine(typeof(Redis.Iris.AgentMemory.Management.AgentMemoryManagementClient).FullName);\n')
PY
dotnet restore "$smoke_dir/Consumer.csproj" --configfile "$smoke_dir/NuGet.Config" --packages "$smoke_dir/packages"
dotnet run --project "$smoke_dir/Consumer.csproj" -c Release --no-restore
