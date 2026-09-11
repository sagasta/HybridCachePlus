# Contributing to HybridCache.Plus

Thank you for your interest in contributing to **HybridCache.Plus**! We welcome contributions, whether they are bug reports, feature suggestions, documentation enhancements, or pull requests.

---

## 🛠️ Development Setup

### Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or higher.
- [Docker Desktop](https://www.docker.com/) (required for running Aspire E2E distributed tests with Redis containers).
- Visual Studio 2022 / 2026, JetBrains Rider, or VS Code with C# Dev Kit.

### Clone and Build
```bash
git clone https://github.com/sagasta/HybridCachePlus.git
cd HybridCachePlus
dotnet restore HybridCachePlus.slnx
dotnet build HybridCachePlus.slnx -c Release
```

---

## 🧪 Running Tests

HybridCache.Plus includes three test tiers:

### 1. Unit & Core Integration Tests
Runs in-memory unit tests, Roslyn generator tests, and broker simulations:
```bash
dotnet test tests/HybridCache.Plus.Tests/HybridCache.Plus.Tests.csproj
```

### 2. NuGet Package Smoke Tests
Verifies that compiled `.nupkg` packages can be consumed from a local feed without internal project references:
```bash
dotnet pack HybridCachePlus.slnx -c Release -o ./local-feed
dotnet test tests/HybridCache.Plus.PackageSmokeTest/HybridCache.Plus.PackageSmokeTest.csproj
```

### 3. Aspire Distributed E2E Tests (Full Multi-Pod & Redis Topology)
Requires Docker running. Automatically orchestrates 4 Redis instances (`backplane`, `shared`, `tenant-alpha`, `tenant-beta`) and 3 API replica pods to validate cross-pod L1 invalidations, stampede protection, and physical tenant isolation:
```bash
# Via PowerShell script:
./playground/run-e2e.ps1

# Or directly:
dotnet build playground/AspirePlayground.slnx
./playground/AspirePlayground.Tests/bin/Debug/net10.0/AspirePlayground.Tests.exe -showLiveOutput
```

---

## 📐 Coding Guidelines

1. **Zero Allocations on Hot Paths**: HybridCache.Plus prioritizes maximum throughput. Avoid heap allocations, string concatenations (`+`), and LINQ in caching or key parsing paths. Use `ReadOnlySpan<char>`, `DefaultInterpolatedStringHandler`, and stack-friendly constructs.
2. **Native AOT Compatibility**: Avoid reflection or runtime type emission. All serialization must use compile-time source-generated `JsonSerializerContext`.
3. **Preserve Roslyn Diagnostics**: When adding attributes or syntax conventions, ensure corresponding analyzers and diagnostic descriptors are updated or added (`HCP001`, `HCP002`, `HCP003`, `HCP004`).
4. **Documentation**: Keep public APIs XML-documented (`<summary>`, `<param>`, `<returns>`).

---

## 🚀 Submitting a Pull Request

1. **Fork the repository** and create your branch from `main`:
   ```bash
   git checkout -b feature/my-new-feature
   ```
2. **Write tests** covering your changes.
3. **Ensure all tests pass** across all test suites.
4. **Commit using Conventional Commits**:
   - `feat: ...` for new features
   - `fix: ...` for bug fixes
   - `docs: ...` for documentation
   - `perf: ...` for performance optimizations
   - `test: ...` for test additions
5. **Open a Pull Request** against `main`. Automated GitHub Actions CI will run your branch and report status.
