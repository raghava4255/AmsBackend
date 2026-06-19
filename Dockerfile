# Stage 1: Build the API
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project files and restore dependencies
COPY ["Ams.csproj", "./"]
RUN dotnet restore "./Ams.csproj"

# Copy the rest of the files and build in release mode
COPY . .
RUN dotnet build "Ams.csproj" -c Release -o /app/build

# Stage 2: Publish
FROM build AS publish
RUN dotnet publish "Ams.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Stage 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Expose port 5073 for backend calls
ENV ASPNETCORE_URLS=http://+:5073
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 5073

ENTRYPOINT ["dotnet", "Ams.dll"]
