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
    <packageSource key="release"><package pattern="Redis.Iris.*" /><package pattern="Microsoft.Agents.AI.Redis.Iris" /></packageSource>
    <packageSource key="nuget"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>''')
p.joinpath('Consumer.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>
<ItemGroup><PackageReference Include="Microsoft.Agents.AI.Redis.Iris" Version="[{escape(version)}]" /></ItemGroup></Project>''')
p.joinpath('Program.cs').write_text('''using Microsoft.Agents.AI.Redis.Iris;
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
