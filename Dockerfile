# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY MarTechSolution.csproj ./
RUN dotnet restore MarTechSolution.csproj
COPY . ./
# Never bake API keys into the image: blank any ApiKey values (keys come from env vars at runtime).
RUN sed -i -E 's/("ApiKey"[[:space:]]*:[[:space:]]*)"[^"]*"/\1""/g' appsettings.json
RUN dotnet publish MarTechSolution.csproj -c Release -o /app --no-restore

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "MarTechSolution.dll"]
