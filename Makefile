.PHONY: setup run build test lint clean publish-macos publish-windows

setup:
	dotnet restore BoxPilot.sln

run:
	dotnet run --project src/BoxPilot.App/BoxPilot.App.csproj

build:
	dotnet build BoxPilot.sln -c Release --no-restore

test:
	dotnet test BoxPilot.sln -c Release --no-restore

lint:
	dotnet format BoxPilot.sln --verify-no-changes --no-restore

clean:
	dotnet clean BoxPilot.sln
	rm -rf dist

publish-macos:
	./scripts/publish.sh osx-arm64
	./scripts/publish.sh osx-x64

publish-windows:
	./scripts/publish.sh win-x64
	./scripts/publish.sh win-arm64
