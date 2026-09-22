#include <windows.h>
#include <shellapi.h>
#ifdef SYLPHYHORN_CLI_LAUNCHER
#include <cstdio>
#endif

#include <string>
#include <vector>

namespace
{
#ifdef SYLPHYHORN_CLI_LAUNCHER
	constexpr wchar_t TargetExecutableName[] = L"sylphyhorn-cli.exe";
#else
	constexpr wchar_t TargetExecutableName[] = L"SylphyHorn.exe";
#endif
	constexpr size_t MaximumSupportedPathLength = MAX_PATH - 1;

	struct PathResult
	{
		std::wstring Path;
		DWORD Error = ERROR_SUCCESS;
	};

	PathResult GetModulePath()
	{
		std::vector<wchar_t> buffer(260);
		for (;;)
		{
			const auto length = GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
			if (length == 0)
			{
				return {{}, GetLastError()};
			}

			if (length < buffer.size() - 1)
			{
				return {std::wstring(buffer.data(), length), ERROR_SUCCESS};
			}

			buffer.resize(buffer.size() * 2);
		}
	}

	std::wstring RemoveExtendedPathPrefix(std::wstring path)
	{
		constexpr wchar_t UncPrefix[] = L"\\\\?\\UNC\\";
		constexpr wchar_t LocalPrefix[] = L"\\\\?\\";
		if (path.rfind(UncPrefix, 0) == 0)
		{
			return L"\\\\" + path.substr((sizeof(UncPrefix) / sizeof(wchar_t)) - 1);
		}

		if (path.rfind(LocalPrefix, 0) == 0)
		{
			return path.substr((sizeof(LocalPrefix) / sizeof(wchar_t)) - 1);
		}

		return path;
	}

	PathResult ResolveExecutablePath(const std::wstring& modulePath)
	{
		const auto handle = CreateFileW(
			modulePath.c_str(),
			0,
			FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
			nullptr,
			OPEN_EXISTING,
			FILE_ATTRIBUTE_NORMAL,
			nullptr);
		if (handle == INVALID_HANDLE_VALUE)
		{
			return {{}, GetLastError()};
		}

		std::vector<wchar_t> buffer(260);
		std::wstring resolvedPath;
		DWORD error = ERROR_SUCCESS;
		for (;;)
		{
			const auto length = GetFinalPathNameByHandleW(
				handle,
				buffer.data(),
				static_cast<DWORD>(buffer.size()),
				FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
			if (length == 0)
			{
				error = GetLastError();
				break;
			}

			if (length < buffer.size())
			{
				resolvedPath.assign(buffer.data(), length);
				break;
			}

			buffer.resize(static_cast<size_t>(length) + 1);
		}

		CloseHandle(handle);
		return {RemoveExtendedPathPrefix(std::move(resolvedPath)), error};
	}

	std::wstring QuoteArgument(const std::wstring& argument)
	{
		if (!argument.empty() && argument.find_first_of(L" \t\n\v\"") == std::wstring::npos)
		{
			return argument;
		}

		std::wstring result = L"\"";
		size_t backslashCount = 0;
		for (const auto character : argument)
		{
			if (character == L'\\')
			{
				++backslashCount;
				continue;
			}

			if (character == L'\"')
			{
				result.append((backslashCount * 2) + 1, L'\\');
				result.push_back(character);
				backslashCount = 0;
				continue;
			}

			result.append(backslashCount, L'\\');
			backslashCount = 0;
			result.push_back(character);
		}

		result.append(backslashCount * 2, L'\\');
		result.push_back(L'\"');
		return result;
	}

	std::wstring BuildCommandLine(const std::wstring& targetPath, DWORD& error)
	{
		error = ERROR_SUCCESS;
		int argumentCount = 0;
		auto arguments = CommandLineToArgvW(GetCommandLineW(), &argumentCount);
		if (arguments == nullptr)
		{
			error = GetLastError();
			return {};
		}

		std::wstring commandLine = QuoteArgument(targetPath);
		for (auto index = 1; index < argumentCount; ++index)
		{
			commandLine.push_back(L' ');
			commandLine.append(QuoteArgument(arguments[index]));
		}

		LocalFree(arguments);
		return commandLine;
	}

	int ShowError(const wchar_t* message, DWORD error)
	{
#ifdef SYLPHYHORN_CLI_LAUNCHER
		fwprintf(stderr, L"%ls Windows error: %lu\n", message, error);
		fputs("{\"schemaVersion\":1,\"command\":null,\"success\":false,"
			"\"error\":{\"code\":\"launcher_failure\","
			"\"message\":\"The CLI could not be started.\",\"retryable\":false}}\n", stdout);
		return 4;
#else
		std::wstring text(message);
		text.append(L"\n\nWindows error: ");
		text.append(std::to_wstring(error));
		MessageBoxW(nullptr, text.c_str(), L"SylphyHornPlus", MB_OK | MB_ICONERROR);
		return static_cast<int>(error == ERROR_SUCCESS ? ERROR_GEN_FAILURE : error);
#endif
	}
}

#ifdef SYLPHYHORN_CLI_LAUNCHER
int wmain()
#else
int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
#endif
{
	const auto modulePathResult = GetModulePath();
	if (modulePathResult.Path.empty())
	{
		return ShowError(
			L"The WinGet launcher path could not be determined.",
			modulePathResult.Error);
	}

	const auto resolvedLauncherPathResult = ResolveExecutablePath(modulePathResult.Path);
	if (resolvedLauncherPathResult.Path.empty())
	{
		return ShowError(
			L"The WinGet launcher target could not be resolved.",
			resolvedLauncherPathResult.Error);
	}

	const auto& resolvedLauncherPath = resolvedLauncherPathResult.Path;
	const auto separator = resolvedLauncherPath.find_last_of(L"\\/");
	if (separator == std::wstring::npos)
	{
		return ShowError(L"The SylphyHornPlus installation directory could not be determined.", ERROR_BAD_PATHNAME);
	}

	const auto installationDirectory = resolvedLauncherPath.substr(0, separator);
	const auto targetPath = installationDirectory + L"\\" + TargetExecutableName;
	if (installationDirectory.size() > MaximumSupportedPathLength ||
		targetPath.size() > MaximumSupportedPathLength)
	{
		return ShowError(
			L"The SylphyHornPlus installation path exceeds the supported Windows path length.",
			ERROR_FILENAME_EXCED_RANGE);
	}
	DWORD commandLineError = ERROR_SUCCESS;
	const auto commandLineText = BuildCommandLine(targetPath, commandLineError);
	if (commandLineText.empty())
	{
		return ShowError(
			L"The SylphyHornPlus command line could not be prepared.",
			commandLineError);
	}

	auto commandLine = std::vector<wchar_t>(commandLineText.begin(), commandLineText.end());
	commandLine.push_back(L'\0');

	STARTUPINFOW startupInfo{};
	startupInfo.cb = sizeof(startupInfo);
#ifdef SYLPHYHORN_CLI_LAUNCHER
	startupInfo.dwFlags = STARTF_USESTDHANDLES;
	startupInfo.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
	startupInfo.hStdOutput = GetStdHandle(STD_OUTPUT_HANDLE);
	startupInfo.hStdError = GetStdHandle(STD_ERROR_HANDLE);
	constexpr BOOL inheritHandles = TRUE;
#else
	constexpr BOOL inheritHandles = FALSE;
#endif
	PROCESS_INFORMATION processInformation{};
	if (!CreateProcessW(
		targetPath.c_str(),
		commandLine.data(),
		nullptr,
		nullptr,
		inheritHandles,
		0,
		nullptr,
		installationDirectory.c_str(),
		&startupInfo,
		&processInformation))
	{
		return ShowError(L"SylphyHornPlus could not be started.", GetLastError());
	}

	CloseHandle(processInformation.hThread);
#ifdef SYLPHYHORN_CLI_LAUNCHER
	const auto waitResult = WaitForSingleObject(processInformation.hProcess, INFINITE);
	DWORD childExitCode = 0;
	if (waitResult != WAIT_OBJECT_0 ||
		!GetExitCodeProcess(processInformation.hProcess, &childExitCode))
	{
		const auto error = GetLastError();
		CloseHandle(processInformation.hProcess);
		return ShowError(L"The CLI result could not be obtained.", error);
	}
	CloseHandle(processInformation.hProcess);
	return static_cast<int>(childExitCode);
#else
	CloseHandle(processInformation.hProcess);
	return 0;
#endif
}
