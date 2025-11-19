FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["SessionService/SessionService.csproj", "SessionService/"]
RUN dotnet restore "SessionService/SessionService.csproj"
COPY SessionService/ SessionService/
COPY version.txt SessionService/
WORKDIR /src/SessionService
RUN dotnet build "SessionService.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "SessionService.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "SessionService.dll"]
