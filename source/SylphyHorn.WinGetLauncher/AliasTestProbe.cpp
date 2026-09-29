#include <windows.h>

#include <cstdint>
#include <cwchar>
#include <string>
#include <vector>

namespace
{
	constexpr wchar_t ResultEnvironmentVariable[] = L"SYLPHYHORN_ALIAS_TEST_RESULT";
	constexpr std::uint32_t FileMagic = 0x31414853;
	constexpr wchar_t TemporarySuffix[] = L".tmp.";

	bool WriteAll(HANDLE file, const void* buffer, DWORD length)
	{
		const auto bytes = static_cast<const BYTE*>(buffer);
		DWORD writtenTotal = 0;
		while (writtenTotal < length)
		{
			DWORD written = 0;
			if (!WriteFile(file, bytes + writtenTotal, length - writtenTotal, &written, nullptr))
			{
				return false;
			}
			writtenTotal += written;
		}
		return true;
	}
}

int wmain(int argumentCount, wchar_t* arguments[])
{
	const auto pathLength = GetEnvironmentVariableW(ResultEnvironmentVariable, nullptr, 0);
	if (pathLength == 0)
	{
		return 2;
	}

	std::vector<wchar_t> path(pathLength);
	if (GetEnvironmentVariableW(
		ResultEnvironmentVariable,
		path.data(),
		static_cast<DWORD>(path.size())) == 0)
	{
		return 3;
	}
	const std::wstring finalPath(path.data());
	const auto processId = GetCurrentProcessId();
	const auto temporaryPath = finalPath + TemporarySuffix + std::to_wstring(processId);

	const auto file = CreateFileW(
		temporaryPath.c_str(),
		GENERIC_WRITE,
		0,
		nullptr,
		CREATE_ALWAYS,
		FILE_ATTRIBUTE_NORMAL,
		nullptr);
	if (file == INVALID_HANDLE_VALUE)
	{
		return 4;
	}

	const auto forwardedCount = static_cast<std::uint32_t>(argumentCount - 1);
	bool succeeded = WriteAll(file, &FileMagic, sizeof(FileMagic)) &&
		WriteAll(file, &processId, sizeof(processId)) &&
		WriteAll(file, &forwardedCount, sizeof(forwardedCount));
	for (auto index = 1; succeeded && index < argumentCount; ++index)
	{
		const auto length = static_cast<std::uint32_t>(wcslen(arguments[index]));
		succeeded = WriteAll(file, &length, sizeof(length)) &&
			WriteAll(file, arguments[index], length * sizeof(wchar_t));
	}

	const auto directoryBufferLength = GetCurrentDirectoryW(0, nullptr);
	std::vector<wchar_t> directory(directoryBufferLength);
	const auto directoryLength = GetCurrentDirectoryW(directoryBufferLength, directory.data());
	succeeded = succeeded && directoryLength > 0 && directoryLength < directoryBufferLength &&
		WriteAll(file, &directoryLength, sizeof(directoryLength)) &&
		WriteAll(file, directory.data(), directoryLength * sizeof(wchar_t));

	CloseHandle(file);
	if (!succeeded)
	{
		DeleteFileW(temporaryPath.c_str());
		return 5;
	}

	if (!MoveFileExW(
		temporaryPath.c_str(),
		finalPath.c_str(),
		MOVEFILE_WRITE_THROUGH))
	{
		DeleteFileW(temporaryPath.c_str());
		return 6;
	}

	return 0;
}
