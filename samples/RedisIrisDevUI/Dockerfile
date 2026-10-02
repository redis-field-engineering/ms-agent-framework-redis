FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore samples/RedisIrisDevUI/RedisIrisDevUI.csproj
RUN dotnet publish samples/RedisIrisDevUI/RedisIrisDevUI.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM build AS context-provisioner
RUN apt-get update \
    && apt-get install --yes --no-install-recommends python3 python3-venv jq \
    && rm -rf /var/lib/apt/lists/* \
    && python3 -m venv /opt/ctxctl \
    && /opt/ctxctl/bin/pip install --no-cache-dir redis-context-retriever==0.0.6
ENV PATH="/opt/ctxctl/bin:${PATH}"
COPY scripts/provision-context-retriever.sh /usr/local/bin/provision-context-retriever
RUN chmod 0755 /usr/local/bin/provision-context-retriever
ENTRYPOINT ["/usr/local/bin/provision-context-retriever"]

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_ENVIRONMENT=Development \
    ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "RedisIrisDevUI.dll"]
