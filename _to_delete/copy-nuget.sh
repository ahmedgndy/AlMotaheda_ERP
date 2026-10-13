#!/bin/bash
# Copies ONLY the NuGet packages this solution already uses (plus the dotnet-ef tool)
# from your local NuGet cache into _to_delete/nuget-offline, so Claude can build and test.
# It changes nothing else. Delete the folder when the work is done.
SRC="$HOME/.nuget/packages"
DST="$(cd "$(dirname "$0")" && pwd)/nuget-offline"
mkdir -p "$DST"
missing=0
while read -r p; do
  [ -z "$p" ] && continue
  if [ -d "$SRC/$p" ]; then mkdir -p "$DST/$(dirname "$p")" && cp -R "$SRC/$p" "$DST/$(dirname "$p")/"; else echo "missing: $p"; missing=$((missing+1)); fi
done <<'LIST'
azure.core/1.47.1
azure.identity/1.14.2
bouncycastle.cryptography/2.4.0
docker.dotnet.enhanced.x509/3.128.3
docker.dotnet.enhanced/3.128.3
dotnet-ef/10.0.10
humanizer.core/2.14.1
microsoft.aspnetcore.authentication.jwtbearer/10.0.9
microsoft.bcl.asyncinterfaces/8.0.0
microsoft.bcl.cryptography/9.0.4
microsoft.build.framework/18.0.2
microsoft.codeanalysis.analyzers/3.11.0
microsoft.codeanalysis.common/5.0.0
microsoft.codeanalysis.csharp.workspaces/5.0.0
microsoft.codeanalysis.csharp/5.0.0
microsoft.codeanalysis.workspaces.common/5.0.0
microsoft.codeanalysis.workspaces.msbuild/5.0.0
microsoft.codecoverage/17.14.1
microsoft.data.sqlclient.sni.runtime/6.0.2
microsoft.data.sqlclient/6.1.1
microsoft.entityframeworkcore.abstractions/10.0.9
microsoft.entityframeworkcore.analyzers/10.0.9
microsoft.entityframeworkcore.design/10.0.9
microsoft.entityframeworkcore.relational/10.0.9
microsoft.entityframeworkcore.sqlserver/10.0.9
microsoft.entityframeworkcore/10.0.9
microsoft.extensions.dependencymodel/10.0.9
microsoft.identity.client.extensions.msal/4.73.1
microsoft.identity.client/4.73.1
microsoft.identitymodel.abstractions/7.7.1
microsoft.identitymodel.abstractions/8.0.1
microsoft.identitymodel.jsonwebtokens/7.7.1
microsoft.identitymodel.jsonwebtokens/8.0.1
microsoft.identitymodel.logging/7.7.1
microsoft.identitymodel.logging/8.0.1
microsoft.identitymodel.protocols.openidconnect/7.7.1
microsoft.identitymodel.protocols.openidconnect/8.0.1
microsoft.identitymodel.protocols/7.7.1
microsoft.identitymodel.protocols/8.0.1
microsoft.identitymodel.tokens/7.7.1
microsoft.identitymodel.tokens/8.0.1
microsoft.net.test.sdk/17.14.1
microsoft.openapi/1.6.23
microsoft.sqlserver.server/1.0.0
microsoft.testplatform.objectmodel/17.14.1
microsoft.testplatform.testhost/17.14.1
microsoft.visualstudio.solutionpersistence/1.0.52
mono.texttemplating/3.0.0
newtonsoft.json/13.0.3
sharpziplib/1.4.2
ssh.net/2024.2.0
swashbuckle.aspnetcore.swagger/9.0.1
swashbuckle.aspnetcore.swaggergen/9.0.1
swashbuckle.aspnetcore.swaggerui/9.0.1
system.clientmodel/1.5.1
system.codedom/6.0.0
system.composition.attributedmodel/9.0.0
system.composition.convention/9.0.0
system.composition.hosting/9.0.0
system.composition.runtime/9.0.0
system.composition.typedparts/9.0.0
system.composition/9.0.0
system.configuration.configurationmanager/9.0.4
system.identitymodel.tokens.jwt/7.7.1
system.identitymodel.tokens.jwt/8.0.1
system.memory.data/8.0.1
system.security.cryptography.pkcs/9.0.4
system.security.cryptography.protecteddata/9.0.4
testcontainers.mssql/4.6.0
testcontainers/4.6.0
xunit.abstractions/2.0.3
xunit.analyzers/1.18.0
xunit.assert/2.9.3
xunit.core/2.9.3
xunit.extensibility.core/2.9.3
xunit.extensibility.execution/2.9.3
xunit.runner.visualstudio/3.1.1
xunit/2.9.3
LIST
echo "Done. Missing packages: $missing"
du -sh "$DST"
