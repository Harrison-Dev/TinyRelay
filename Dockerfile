# Stage 1: Build the application
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build

# Set the working directory inside the container
WORKDIR /src

# Copy the solution file and project file
COPY TinyRelay.sln ./
COPY TinyRelay/TinyRelay.csproj TinyRelay/

# Restore dependencies
RUN dotnet restore TinyRelay/TinyRelay.csproj

# Copy the entire source code
COPY TinyRelay/ TinyRelay/

# Build the project
RUN dotnet publish TinyRelay/TinyRelay.csproj -c Release -o /app/publish

# Stage 2: Create the runtime image
FROM mcr.microsoft.com/dotnet/runtime:8.0-alpine AS runtime

# Set the working directory inside the container
WORKDIR /app

# Copy the published output from the build stage
COPY --from=build /app/publish .

# Expose the port your application listens on
EXPOSE 9050

# Set the entry point for the application
ENTRYPOINT ["dotnet", "TinyRelay.dll"]
