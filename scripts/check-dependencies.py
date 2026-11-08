"""Fail CI on known vulnerable NuGet dependencies, including transitive dependencies."""
import json
import subprocess
import sys

result = subprocess.run(["dotnet", "list", "src/MemoryForge.Server", "package", "--vulnerable", "--include-transitive", "--format", "json"], capture_output=True, text=True, check=True)
report = json.loads(result.stdout)
vulnerable = []
for project in report.get("projects", []):
    for framework in project.get("frameworks", []):
        for group in ("topLevelPackages", "transitivePackages"):
            for package in framework.get(group, []):
                if package.get("vulnerabilities"):
                    vulnerable.append(package)
if vulnerable:
    print(json.dumps(vulnerable, indent=2))
    sys.exit(1)
print("No known vulnerable server dependencies.")
