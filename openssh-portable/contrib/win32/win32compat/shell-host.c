/*
 * Author: Manoj Ampalam <manoj.ampalam@microsoft.com>
 * Primitive shell-host to support parsing of cmd.exe input and async IO redirection
 *
 * Author: Ray Heyes <ray.hayes@microsoft.com>
 * PTY with ANSI emulation wrapper
 *
 * Copyright (c) 2017 Microsoft Corp.
 * All rights reserved
 *
 * Shell-host is responsible for handling all the interactive and non-interactive cmds.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions
 * are met:
 *
 * 1. Redistributions of source code must retain the above copyright
 * notice, this list of conditions and the following disclaimer.
 * 2. Redistributions in binary form must reproduce the above copyright
 * notice, this list of conditions and the following disclaimer in the
 * documentation and/or other materials provided with the distribution.
 *
 * THIS SOFTWARE IS PROVIDED BY THE AUTHOR ``AS IS'' AND ANY EXPRESS OR
 * IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES
 * OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED.
 * IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY DIRECT, INDIRECT,
 * INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT
 * NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
 * DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
 * THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF
 * THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

#include <Windows.h>
#include <Strsafe.h>
#include <stdio.h>
#include <io.h>
#include <Shlobj.h>
#include <Sddl.h>
#include <process.h>
#include "misc_internal.h"
#include "inc\utf.h"

#define MAX_CONSOLE_COLUMNS 9999
#define MAX_CONSOLE_ROWS 9999
#define WM_APPEXIT WM_USER+1
#define MAX_EXPECTED_BUFFER_SIZE 1024
/* 4KB is the largest size for which writes are guaranteed to be atomic */
#define BUFF_SIZE 4096

#ifndef ENABLE_VIRTUAL_TERMINAL_PROCESSING
#define ENABLE_VIRTUAL_TERMINAL_PROCESSING  0x4
#endif

#ifndef ENABLE_VIRTUAL_TERMINAL_INPUT
#define ENABLE_VIRTUAL_TERMINAL_INPUT 0x0200
#endif

#define VK_A 0x41
#define VK_B 0x42
#define VK_C 0x43
#define VK_D 0x44
#define VK_E 0x45
#define VK_F 0x46
#define VK_G 0x47
#define VK_H 0x48
#define VK_I 0x49
#define VK_J 0x4A
#define VK_K 0x4B
#define VK_L 0x4C
#define VK_M 0x4D
#define VK_N 0x4E
#define VK_O 0x4F
#define VK_P 0x50
#define VK_Q 0x51
#define VK_R 0x52
#define VK_S 0x53
#define VK_T 0x54
#define VK_U 0x55
#define VK_V 0x56
#define VK_W 0x57
#define VK_X 0x58
#define VK_Y 0x59
#define VK_Z 0x5A
#define VK_0 0x30
#define VK_1 0x31
#define VK_2 0x32
#define VK_3 0x33
#define VK_4 0x34
#define VK_5 0x35
#define VK_6 0x36
#define VK_7 0x37
#define VK_8 0x38
#define VK_9 0x39

const int MAX_CTRL_SEQ_LEN = 7;
const int MIN_CTRL_SEQ_LEN = 6;

typedef BOOL(WINAPI *__t_SetCurrentConsoleFontEx)(
	_In_ HANDLE               hConsoleOutput,
	_In_ BOOL                 bMaximumWindow,
	_In_ PCONSOLE_FONT_INFOEX lpConsoleCurrentFontEx
	);
__t_SetCurrentConsoleFontEx __SetCurrentConsoleFontEx;

typedef BOOL(WINAPI *__t_UnhookWinEvent)(
	_In_ HWINEVENTHOOK hWinEventHook
	);
__t_UnhookWinEvent __UnhookWinEvent;

typedef HWINEVENTHOOK(WINAPI *__t_SetWinEventHook)(
	_In_ UINT         eventMin,
	_In_ UINT         eventMax,
	_In_ HMODULE      hmodWinEventProc,
	_In_ WINEVENTPROC lpfnWinEventProc,
	_In_ DWORD        idProcess,
	_In_ DWORD        idThread,
	_In_ UINT         dwflags
	);
__t_SetWinEventHook __SetWinEventHook;

typedef struct consoleEvent {
	DWORD event;
	HWND  hwnd;
	LONG  idObject;
	LONG  idChild;
	void* prior;
	void* next;
} consoleEvent;

struct key_translation {
	wchar_t in[8];
	int vk;
	wchar_t out;
	int in_key_len;
	DWORD ctrlState;
} key_translation;

/* All the substrings should be in the end, otherwise ProcessIncomingKeys() will not work as expected */
struct key_translation keys[] = {
    { L"\r",         VK_RETURN,  L'\r', 0, 0},
    { L"\n",         VK_RETURN,  L'\r', 0, 0 },
    { L"\b",         VK_BACK,    L'\b', 0, 0 },
    { L"\x7f",       VK_BACK,    L'\b', 0 , 0 },
    { L"\t",         VK_TAB,     L'\t' , 0 , 0},
    { L"\x1b[A",     VK_UP,       0 , 0 , 0},
    { L"\x1b[B",     VK_DOWN,     0 , 0 , 0},
    { L"\x1b[C",     VK_RIGHT,    0 , 0 , 0},
    { L"\x1b[D",     VK_LEFT,     0 , 0 , 0},
    { L"\x1b[F",     VK_END,      0 , 0 , 0},    /* KeyPad END */
    { L"\x1b[H",     VK_HOME,     0 , 0 , 0},    /* KeyPad HOME */
    { L"\x1b[Z",     VK_TAB,     L'\t' , 0 , SHIFT_PRESSED},
    { L"\x1b[1~",    VK_HOME,     0 , 0 , 0},
    { L"\x1b[2~",    VK_INSERT,   0 , 0 , 0},
    { L"\x1b[3~",    VK_DELETE,   0 , 0 , 0},
    { L"\x1b[4~",    VK_END,      0 , 0 , 0},
    { L"\x1b[5~",    VK_PRIOR,    0 , 0 , 0},
    { L"\x1b[6~",    VK_NEXT,     0 , 0 , 0},
    { L"\x1b[11~",   VK_F1,       0 , 0 , 0},
    { L"\x1b[12~",   VK_F2,       0 , 0 , 0},
    { L"\x1b[13~",   VK_F3,       0 , 0 , 0},
    { L"\x1b[14~",   VK_F4,       0 , 0 , 0},
    { L"\x1b[15~",   VK_F5,       0 , 0 , 0},
    { L"\x1b[17~",   VK_F6,       0 , 0 , 0},
    { L"\x1b[18~",   VK_F7,       0 , 0 , 0},
    { L"\x1b[19~",   VK_F8,       0 , 0 , 0},
    { L"\x1b[20~",   VK_F9,       0 , 0 , 0},
    { L"\x1b[21~",   VK_F10,      0 , 0 , 0},
    { L"\x1b[23~",   VK_F11,      0 , 0 , 0},
    { L"\x1b[24~",   VK_F12,      0 , 0 , 0},
    { L"\x1bOA",     VK_UP,       0 , 0 , 0},
    { L"\x1bOB",     VK_DOWN,     0 , 0 , 0},
    { L"\x1bOC",     VK_RIGHT,    0 , 0 , 0},
    { L"\x1bOD",     VK_LEFT,     0 , 0 , 0},
    { L"\x1bOF",     VK_END,      0 , 0 , 0},    /* KeyPad END */
    { L"\x1bOH",     VK_HOME,     0 , 0 , 0},    /* KeyPad HOME */
    { L"\x1bOP",     VK_F1,       0 , 0 , 0},
    { L"\x1bOQ",     VK_F2,       0 , 0 , 0},
    { L"\x1bOR",     VK_F3,       0 , 0 , 0},
    { L"\x1bOS",     VK_F4,       0 , 0 , 0},
    { L"\x1",        VK_A,   L'\x1' , 0 , LEFT_CTRL_PRESSED},
    { L"\x2",        VK_B,   L'\x2' , 0 , LEFT_CTRL_PRESSED},
    //{ L"\x3",        VK_C,   L'\x3' , 0 , LEFT_CTRL_PRESSED}, /* Control + C is handled differently */
    { L"\x4",        VK_D,   L'\x4' , 0 , LEFT_CTRL_PRESSED},
    { L"\x5",        VK_E,   L'\x5' , 0 , LEFT_CTRL_PRESSED},
    { L"\x6",        VK_F,   L'\x6' , 0 , LEFT_CTRL_PRESSED},
    { L"\x7",        VK_G,   L'\x7' , 0 , LEFT_CTRL_PRESSED},
    { L"\x8",        VK_H,   L'\x8' , 0 , LEFT_CTRL_PRESSED},
    { L"\x9",        VK_I,   L'\x9' , 0 , LEFT_CTRL_PRESSED},
    { L"\xA",        VK_J,   L'\xA' , 0 , LEFT_CTRL_PRESSED},
    { L"\xB",        VK_K,   L'\xB' , 0 , LEFT_CTRL_PRESSED},
    { L"\xC",        VK_L,   L'\xC' , 0 , LEFT_CTRL_PRESSED},
    { L"\xD",        VK_M,   L'\xD' , 0 , LEFT_CTRL_PRESSED},
    { L"\xE",        VK_N,   L'\xE' , 0 , LEFT_CTRL_PRESSED},
    { L"\xF",        VK_O,   L'\xF' , 0 , LEFT_CTRL_PRESSED},
    { L"\x10",       VK_P,   L'\x10' , 0 , LEFT_CTRL_PRESSED},
    { L"\x11",       VK_Q,   L'\x11' , 0 , LEFT_CTRL_PRESSED},
    { L"\x12",       VK_R,   L'\x12' , 0 , LEFT_CTRL_PRESSED},
    { L"\x13",       VK_S,   L'\x13' , 0 , LEFT_CTRL_PRESSED},
    { L"\x14",       VK_T,   L'\x14' , 0 , LEFT_CTRL_PRESSED},
    { L"\x15",       VK_U,   L'\x15' , 0 , LEFT_CTRL_PRESSED},
    { L"\x16",       VK_V,   L'\x16' , 0 , LEFT_CTRL_PRESSED},
    { L"\x17",       VK_W,   L'\x17' , 0 , LEFT_CTRL_PRESSED},
    { L"\x18",       VK_X,   L'\x18' , 0 , LEFT_CTRL_PRESSED},
    { L"\x19",       VK_Y,   L'\x19' , 0 , LEFT_CTRL_PRESSED},
    { L"\x1A",       VK_Z,   L'\x1A' , 0 , LEFT_CTRL_PRESSED},
    { L"\033a",      VK_A,   L'a', 0, LEFT_ALT_PRESSED},
    { L"\033b",      VK_B,   L'b', 0, LEFT_ALT_PRESSED},
    { L"\033c",      VK_C,   L'c', 0, LEFT_ALT_PRESSED},
    { L"\033d",      VK_D,   L'd', 0, LEFT_ALT_PRESSED},
    { L"\033e",      VK_E,   L'e', 0, LEFT_ALT_PRESSED},
    { L"\033f",      VK_F,   L'f', 0, LEFT_ALT_PRESSED},
    { L"\033g",      VK_G,   L'g', 0, LEFT_ALT_PRESSED},
    { L"\033h",      VK_H,   L'h', 0, LEFT_ALT_PRESSED},
    { L"\033i",      VK_I,   L'i', 0, LEFT_ALT_PRESSED},
    { L"\033j",      VK_J,   L'j', 0, LEFT_ALT_PRESSED},
    { L"\033k",      VK_K,   L'k', 0, LEFT_ALT_PRESSED},
    { L"\033l",      VK_L,   L'l', 0, LEFT_ALT_PRESSED},
    { L"\033m",      VK_M,   L'm', 0, LEFT_ALT_PRESSED},
    { L"\033n",      VK_N,   L'n', 0, LEFT_ALT_PRESSED},
    { L"\033o",      VK_O,   L'o', 0, LEFT_ALT_PRESSED},
    { L"\033p",      VK_P,   L'p', 0, LEFT_ALT_PRESSED},
    { L"\033q",      VK_Q,   L'q', 0, LEFT_ALT_PRESSED},
    { L"\033r",      VK_R,   L'r', 0, LEFT_ALT_PRESSED},
    { L"\033s",      VK_S,   L's', 0, LEFT_ALT_PRESSED},
    { L"\033t",      VK_T,   L't', 0, LEFT_ALT_PRESSED},
    { L"\033u",      VK_U,   L'u', 0, LEFT_ALT_PRESSED},
    { L"\033v",      VK_V,   L'v', 0, LEFT_ALT_PRESSED},
    { L"\033w",      VK_W,   L'w', 0, LEFT_ALT_PRESSED},
    { L"\033x",      VK_X,   L'x', 0, LEFT_ALT_PRESSED},
    { L"\033y",      VK_Y,   L'y', 0, LEFT_ALT_PRESSED},
    { L"\033z",      VK_Z,   L'z', 0, LEFT_ALT_PRESSED},
    { L"\0330",      VK_0,   L'0', 0, LEFT_ALT_PRESSED},
    { L"\0331",      VK_1,   L'1', 0, LEFT_ALT_PRESSED},
    { L"\0332",      VK_2,   L'2', 0, LEFT_ALT_PRESSED},
    { L"\0333",      VK_3,   L'3', 0, LEFT_ALT_PRESSED},
    { L"\0334",      VK_4,   L'4', 0, LEFT_ALT_PRESSED},
    { L"\0335",      VK_5,   L'5', 0, LEFT_ALT_PRESSED},
    { L"\0336",      VK_6,   L'6', 0, LEFT_ALT_PRESSED},
    { L"\0337",      VK_7,   L'7', 0, LEFT_ALT_PRESSED},
    { L"\0338",      VK_8,   L'8', 0, LEFT_ALT_PRESSED},
    { L"\0339",      VK_9,   L'9', 0, LEFT_ALT_PRESSED},
    { L"\033!",      VK_1,   L'!', 0, LEFT_ALT_PRESSED | SHIFT_PRESSED },
    { L"\033@",      VK_2,   L'@', 0, LEFT_ALT_PRESSED | SHIFT_PRESSED },
    { L"\033#",      VK_3,   L'#', 0, LEFT_ALT_PRESSED | SHIFT_PRESSED },
    { L"\033$",      VK_4,   L'$', 0, LEFT_ALT_PRESSED | SHIFT_PRESSED },
    { L"\033%",      VK_5,   L'%', 0, LEFT_ALT_PRESSED | SHIFT_PRESSED },
    { L"\033^",      VK_6,   L'^', 0, LEFT_ALT_PRESSED | SHIFT_PRESSED },
    { L"\033&",      VK_7,   L'&', 0, LEFT_ALT_PRESSED | SHIFT_PRESSED },
    { L"\033*",      VK_8,   L'*', 0, LEFT_ALT_PRESSED | SHIFT_PRESSED },
    { L"\033(",      VK_9,   L'(', 0, LEFT_ALT_PRESSED | SHIFT_PRESSED },
    { L"\033)",      VK_0,   L')', 0, LEFT_ALT_PRESSED | SHIFT_PRESSED }
};

static SHORT lastX = 0;
static SHORT lastY = 0;
static wchar_t system32_path[PATH_MAX + 1] = { 0, };

SHORT currentLine = 0;
consoleEvent* head = NULL;
consoleEvent* tail = NULL;

BOOL bRet = FALSE;
BOOL bNoScrollRegion = FALSE;
BOOL bStartup = TRUE;
BOOL bHookEvents = FALSE;
BOOL bFullScreen = FALSE;
BOOL bUseAnsiEmulation = TRUE;

HANDLE child_out = INVALID_HANDLE_VALUE;
HANDLE child_in = INVALID_HANDLE_VALUE;
HANDLE child_err = INVALID_HANDLE_VALUE;
HANDLE pipe_in = INVALID_HANDLE_VALUE;
HANDLE pipe_out = INVALID_HANDLE_VALUE;
HANDLE pipe_ctrl = INVALID_HANDLE_VALUE;
HANDLE child = INVALID_HANDLE_VALUE;
HANDLE job = NULL;
HANDLE hConsoleBuffer = INVALID_HANDLE_VALUE;
HANDLE monitor_thread = INVALID_HANDLE_VALUE;
HANDLE io_thread = INVALID_HANDLE_VALUE;
HANDLE ux_thread = INVALID_HANDLE_VALUE;
HANDLE ctrl_thread = INVALID_HANDLE_VALUE;

DWORD child_exit_code = 0;
DWORD hostProcessId = 0;
DWORD hostThreadId = 0;
DWORD childProcessId = 0;
DWORD dwStatus = 0;
DWORD in_cmd_len = 0;
DWORD lastLineLength = 0;

UINT cp = 0;
UINT ViewPortY = 0;
UINT lastViewPortY = 0;
UINT savedViewPortY = 0;
UINT savedLastViewPortY = 0;

char in_cmd[MAX_CMD_LEN];

CRITICAL_SECTION criticalSection;

CONSOLE_SCREEN_BUFFER_INFOEX  consoleInfo;
CONSOLE_SCREEN_BUFFER_INFOEX  nextConsoleInfo;
STARTUPINFO inputSi;

#define GOTO_CLEANUP_ON_FALSE(exp) do {	\
	ret = (exp);			\
	if (ret == FALSE)		\
		goto cleanup;		\
} while(0)

#define GOTO_CLEANUP_ON_ERR(exp) do {	\
	if ((exp) != 0)			\
		goto cleanup;		\
} while(0)

void     
debug3(const char *s, ...) {
	return;
}

int
ConSRWidth()
{
	CONSOLE_SCREEN_BUFFER_INFOEX  consoleBufferInfo;
	ZeroMemory(&consoleBufferInfo, sizeof(consoleBufferInfo));
	consoleBufferInfo.cbSize = sizeof(consoleBufferInfo);

	GetConsoleScreenBufferInfoEx(child_out, &consoleBufferInfo);
	return consoleBufferInfo.srWindow.Right;
}

void
my_invalid_parameter_handler(const wchar_t* expression, const wchar_t* function,
	 const wchar_t* file, unsigned int line, uintptr_t pReserved)
{
	wprintf_s(L"Invalid parameter in function: %s. File: %s Line: %d\n", function, file, line);
	wprintf_s(L"Expression: %s\n", expression);
}

struct key_translation *
FindKeyTransByMask(wchar_t prefix, const wchar_t * value, int vlen, wchar_t suffix)
{
	struct key_translation *k = NULL;
	for (int i = 0; i < ARRAYSIZE(keys); i++) {
		k = &keys[i];
		if (k->in_key_len < vlen + 2) continue;
		if (k->in[0] != L'\033') continue;
		if (k->in[1] != prefix) continue;
		if (k->in[vlen + 2] != suffix) continue;

		if (vlen <= 1 && value[0] == k->in[2])
			return k;
		if (vlen > 1 && wcsncmp(&k->in[2], value, vlen) == 0)
			return k;
	}

	return NULL;
}

int
GetVirtualKeyByMask(wchar_t prefix, const wchar_t * value, int vlen, wchar_t suffix)
{
	struct key_translation * pk = FindKeyTransByMask(prefix, value, vlen, suffix);
	return pk ? pk->vk : 0;
}

/*
 * This function will handle the console keystrokes.
 */
void
SendKeyStrokeEx(HANDLE hInput, int vKey, wchar_t character, DWORD ctrlState, BOOL keyDown)
{
	DWORD wr = 0;
	INPUT_RECORD ir;

	ir.EventType = KEY_EVENT;
	ir.Event.KeyEvent.bKeyDown = keyDown;
	ir.Event.KeyEvent.wRepeatCount = 1;
	ir.Event.KeyEvent.wVirtualKeyCode = vKey;
	ir.Event.KeyEvent.wVirtualScanCode = MapVirtualKeyA(vKey, MAPVK_VK_TO_VSC);
	ir.Event.KeyEvent.dwControlKeyState = ctrlState;
	ir.Event.KeyEvent.uChar.UnicodeChar = character;

	WriteConsoleInputW(hInput, &ir, 1, &wr);
}

void
SendKeyStroke(HANDLE hInput, int keyStroke, wchar_t character, DWORD ctrlState)
{
	SendKeyStrokeEx(hInput, keyStroke, character, ctrlState, TRUE);
	SendKeyStrokeEx(hInput, keyStroke, character, ctrlState, FALSE);
}

void
initialize_keylen()
{
	for(int i = 0; i < ARRAYSIZE(keys); i++)
		keys[i].in_key_len = (int) wcsnlen(keys[i].in, _countof(keys[i].in));
}

int
ProcessModifierKeySequence(wchar_t *buf, int buf_len)
{
	if(buf_len < MIN_CTRL_SEQ_LEN)
		return 0;

	int vkey = 0;	
	int modifier_key = _wtoi((wchar_t *)&buf[buf_len - 2]);

	if ((modifier_key < 2) && (modifier_key > 7))
		return 0;

	/* Decode special keys when pressed ALT/CTRL/SHIFT key */
	if (buf[0] == L'\033' && buf[1] == L'[' && buf[buf_len - 3] == L';') {
		if (buf[buf_len - 1] == L'~') {
			/* VK_DELETE, VK_PGDN, VK_PGUP */
			if (!vkey && buf_len == 6)
				vkey = GetVirtualKeyByMask(L'[', &buf[2], 1, L'~');

			/* VK_F1 ... VK_F12 */
			if (!vkey && buf_len == 7)
				vkey = GetVirtualKeyByMask(L'[', &buf[2], 2, L'~');
		} else {
			/* VK_LEFT, VK_RIGHT, VK_UP, VK_DOWN */
			if (!vkey && buf_len == 6 && buf[2] == L'1')
				vkey = GetVirtualKeyByMask(L'[', &buf[5], 1, 0);

			/* VK_F1 ... VK_F4 */
			if (!vkey && buf_len == 6 && buf[2] == L'1' && isalpha(buf[5]))
				vkey = GetVirtualKeyByMask(L'O', &buf[5], 1, 0);
		}
		if (vkey) {
			switch (modifier_key)
			{
				case 2:
					SendKeyStroke(child_in, vkey, 0, SHIFT_PRESSED);
					break;
				case 3:
					SendKeyStroke(child_in, vkey, 0, LEFT_ALT_PRESSED);
					break;
				case 4:
					SendKeyStroke(child_in, vkey, 0, SHIFT_PRESSED | LEFT_ALT_PRESSED);
					break;
				case 5:
					SendKeyStroke(child_in, vkey, 0, LEFT_CTRL_PRESSED);
					break;
				case 6:
					SendKeyStroke(child_in, vkey, 0, SHIFT_PRESSED | LEFT_CTRL_PRESSED);
					break;
				case 7:
					SendKeyStroke(child_in, vkey, 0, LEFT_CTRL_PRESSED | LEFT_ALT_PRESSED);
					break;				
			}
		}
			
	}

	return vkey;
}
int
CheckKeyTranslations(wchar_t *buf, int buf_len, int *index)
{
	for (int j = 0; j < ARRAYSIZE(keys); j++) {
		if ((buf_len >= keys[j].in_key_len) && (wcsncmp(buf, keys[j].in, keys[j].in_key_len) == 0)) {
			*index = j;
			return 1;
		}
	}

	return 0;
}

void 
ProcessIncomingKeys(char * ansikey)
{
	int buf_len = 0;
	const wchar_t *ESC_SEQ = L"\x1b";
	wchar_t *buf = utf8_to_utf16(ansikey);

	if (!buf) {
		printf_s("\nFailed to deserialize the client data, error:%d\n", GetLastError());
		exit(255);
	}

	loop:
	while (buf && ((buf_len=(int)wcslen(buf)) > 0)) {
		int j = 0;
		if (CheckKeyTranslations(buf, buf_len, &j)) {
			SendKeyStroke(child_in, keys[j].vk, keys[j].out, keys[j].ctrlState);				
			buf += keys[j].in_key_len;
			goto loop;
		}

		/* Decode special keys when pressed CTRL key. CTRL sequences can be of size 6 or 7. */
		if ((buf_len >= MAX_CTRL_SEQ_LEN) && ProcessModifierKeySequence(buf, MAX_CTRL_SEQ_LEN)) {
			buf += MAX_CTRL_SEQ_LEN;
			goto loop;
		}

		if ((buf_len >= (MAX_CTRL_SEQ_LEN - 1)) && ProcessModifierKeySequence(buf, MAX_CTRL_SEQ_LEN - 1)) {
			buf += (MAX_CTRL_SEQ_LEN - 1);
			goto loop;
		}

		if(wcsncmp(buf, ESC_SEQ, wcslen(ESC_SEQ)) == 0) {
			wchar_t* p = buf + wcslen(ESC_SEQ);
			/* Alt sequence */
			if (CheckKeyTranslations(p, buf_len - (int)wcslen(ESC_SEQ), &j) && !(keys[j].ctrlState & LEFT_ALT_PRESSED)) {
				SendKeyStroke(child_in, keys[j].vk, keys[j].out, keys[j].ctrlState| LEFT_ALT_PRESSED);
				buf += wcslen(ESC_SEQ) +keys[j].in_key_len;
				goto loop;
			}

			SendKeyStroke(child_in, VK_ESCAPE, L'\x1b', 0);
			buf += wcslen(ESC_SEQ);
			goto loop;
		}

		if (*buf == L'\x3') /*Ctrl+C - Raise Ctrl+C*/
			GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0);
		else 
			SendKeyStroke(child_in, 0, *buf, 0);

		buf++;
	}		
}

/*
 * VT output routines
 */
void 
SendLF(HANDLE hInput)
{
	DWORD wr = 0;

	if (bUseAnsiEmulation)
		WriteFile(hInput, "\n", 1, &wr, NULL);
}

void 
SendClearScreen(HANDLE hInput)
{
	DWORD wr = 0;

	if (bUseAnsiEmulation)
		WriteFile(hInput, "\033[2J", 4, &wr, NULL);
}

void 
SendClearScreenFromCursor(HANDLE hInput)
{
	DWORD wr = 0;

	if (bUseAnsiEmulation)
		WriteFile(hInput, "\033[1J", 4, &wr, NULL);
}

void 
SendHideCursor(HANDLE hInput)
{
	DWORD wr = 0;

	if (bUseAnsiEmulation)
		WriteFile(hInput, "\033[?25l", 6, &wr, NULL);
}

void 
SendShowCursor(HANDLE hInput)
{
	DWORD wr = 0;

	if (bUseAnsiEmulation)
		WriteFile(hInput, "\033[?25h", 6, &wr, NULL);
}

void 
SendCursorPositionRequest(HANDLE hInput)
{
	DWORD wr = 0;

	if (bUseAnsiEmulation)
		WriteFile(hInput, "\033[6n", 4, &wr, NULL);
}

void 
SendSetCursor(HANDLE hInput, int X, int Y)
{
	DWORD wr = 0;
	int out = 0;
	char formatted_output[255];

	out = _snprintf_s(formatted_output, sizeof(formatted_output), _TRUNCATE, "\033[%d;%dH", Y, X);
	if (out > 0 && bUseAnsiEmulation)
		WriteFile(hInput, formatted_output, out, &wr, NULL);
}

void 
SendVerticalScroll(HANDLE hInput, int lines)
{
	DWORD wr = 0;
	int out = 0;
	char formatted_output[255];

	LONG vn = abs(lines);
	/* Not supporting the [S at the moment. */
	if (lines > 0) {
		out = _snprintf_s(formatted_output, sizeof(formatted_output), _TRUNCATE, "\033[%dT", vn);

		if (out > 0 && bUseAnsiEmulation)
			WriteFile(hInput, formatted_output, out, &wr, NULL);
	}	
}

void 
SendHorizontalScroll(HANDLE hInput, int cells)
{
	DWORD wr = 0;
	int out = 0;
	char formatted_output[255];

	out = _snprintf_s(formatted_output, sizeof(formatted_output), _TRUNCATE, "\033[%dG", cells);

	if (out > 0 && bUseAnsiEmulation)
		WriteFile(hInput, formatted_output, out, &wr, NULL);
}

void 
SendCharacter(HANDLE hInput, WORD attributes, wchar_t character)
{
	DWORD wr = 0;
	DWORD out = 0;
	DWORD current = 0;
	char formatted_output[2048];
	static WORD pattributes = 0;
	USHORT Color = 0;
	ULONG Status = 0;
	PSTR Next;
	size_t SizeLeft;

	if (!character)
		return;

	Next = formatted_output;
	SizeLeft = sizeof formatted_output;

	/* Handle the foreground intensity */
	if ((attributes & FOREGROUND_INTENSITY) != 0)
		Color = 1;
	else
		Color = 0;

	StringCbPrintfExA(Next, SizeLeft, &Next, &SizeLeft, 0, "\033[%u", Color);

	/* Handle the background intensity */
	if ((attributes & BACKGROUND_INTENSITY) != 0)
		Color = 1;
	else
		Color = 39;

	StringCbPrintfExA(Next, SizeLeft, &Next, &SizeLeft, 0, ";%u", Color);

	/* Handle the underline */
	if ((attributes & COMMON_LVB_UNDERSCORE) != 0)
		Color = 4;
	else
		Color = 24;

	StringCbPrintfExA(Next, SizeLeft, &Next, &SizeLeft, 0, ";%u", Color);

	/* Handle reverse video */
	if ((attributes & COMMON_LVB_REVERSE_VIDEO) != 0)
		Color = 7;
	else
		Color = 27;

	StringCbPrintfExA(Next, SizeLeft, &Next, &SizeLeft, 0, ";%u", Color);

	/* Add background and foreground colors to buffer. */
	Color = 30 +
		4 * ((attributes & FOREGROUND_BLUE) != 0) +
		2 * ((attributes & FOREGROUND_GREEN) != 0) +
		1 * ((attributes & FOREGROUND_RED) != 0);

	StringCbPrintfExA(Next, SizeLeft, &Next, &SizeLeft, 0, ";%u", Color);

	Color = 40 +
		4 * ((attributes & BACKGROUND_BLUE) != 0) +
		2 * ((attributes & BACKGROUND_GREEN) != 0) +
		1 * ((attributes & BACKGROUND_RED) != 0);

	StringCbPrintfExA(Next, SizeLeft, &Next, &SizeLeft, 0, ";%u", Color);
	
	StringCbPrintfExA(Next, SizeLeft, &Next, &SizeLeft, 0, "%c", 'm');

	if (bUseAnsiEmulation && attributes != pattributes)
		WriteFile(hInput, formatted_output, (DWORD)(Next - formatted_output), &wr, NULL);

	/* East asian languages have 2 bytes for each character, only use the first */
	if (!(attributes & COMMON_LVB_TRAILING_BYTE)) {
		char str[10];
		int nSize = WideCharToMultiByte(CP_UTF8,
			0,
			&character,
			1,
			(LPSTR)str,
			sizeof(str),
			NULL,
			NULL);

		if (nSize > 0)
			WriteFile(hInput, str, nSize, &wr, NULL);
	}

	pattributes = attributes;
}

void 
SendBuffer(HANDLE hInput, CHAR_INFO *buffer, DWORD bufferSize)
{
	if (bufferSize <= 0)
		return;

	for (DWORD i = 0; i < bufferSize; i++)
		SendCharacter(hInput, buffer[i].Attributes, buffer[i].Char.UnicodeChar);
}

void 
CalculateAndSetCursor(HANDLE hInput, short x, short y, BOOL scroll)
{
	if (scroll && y > currentLine)
		for (short n = currentLine; n < y; n++)
			SendLF(hInput);

	SendSetCursor(hInput, x + 1, y + 1);
	currentLine = y;
}

void 
SizeWindow(HANDLE hInput)
{
	SMALL_RECT srWindowRect;
	COORD coordScreen;
	BOOL bSuccess = FALSE;
	/* The input window does not scroll currently to ease calculations on the paint/draw */
	bNoScrollRegion = TRUE;

	/* Set the default font to Consolas */
	CONSOLE_FONT_INFOEX matchingFont;
	matchingFont.cbSize = sizeof(matchingFont);
	matchingFont.nFont = 0;
	matchingFont.dwFontSize.X = 0;
	matchingFont.dwFontSize.Y = 16;
	matchingFont.FontFamily = FF_DONTCARE;
	matchingFont.FontWeight = FW_NORMAL;	
	wcscpy_s(matchingFont.FaceName, LF_FACESIZE, L"Consolas");

	bSuccess = __SetCurrentConsoleFontEx(hInput, FALSE, &matchingFont);

	/* This information is the live screen  */
	ZeroMemory(&consoleInfo, sizeof(consoleInfo));
	consoleInfo.cbSize = sizeof(consoleInfo);

	bSuccess = GetConsoleScreenBufferInfoEx(hInput, &consoleInfo);

	/* Get the largest size we can size the console window to */
	coordScreen = GetLargestConsoleWindowSize(hInput);

	/* Define the new console window size and scroll position */
	if (inputSi.dwXCountChars == 0 || inputSi.dwYCountChars == 0) {
		inputSi.dwXCountChars = 80;
		inputSi.dwYCountChars = 25;
	}

	srWindowRect.Right = min((SHORT)inputSi.dwXCountChars, coordScreen.X) - 1;
	srWindowRect.Bottom = min((SHORT)inputSi.dwYCountChars, coordScreen.Y) - 1;
	srWindowRect.Left = srWindowRect.Top = (SHORT)0;

	/* Define the new console buffer history to be the maximum possible */
	coordScreen.X = srWindowRect.Right + 1;   /* buffer width must be equ window width */
	coordScreen.Y = 9999;

	if (SetConsoleWindowInfo(hInput, TRUE, &srWindowRect))
		bSuccess = SetConsoleScreenBufferSize(hInput, coordScreen);
	else {
		if (SetConsoleScreenBufferSize(hInput, coordScreen))
			bSuccess = SetConsoleWindowInfo(hInput, TRUE, &srWindowRect);
	}

	bSuccess = GetConsoleScreenBufferInfoEx(hInput, &consoleInfo);
}

unsigned __stdcall
MonitorChild(_In_ LPVOID lpParameter)
{
	WaitForSingleObject(child, INFINITE);
	GetExitCodeProcess(child, &child_exit_code);
	PostThreadMessage(hostThreadId, WM_APPEXIT, 0, 0);
	return 0;
}

unsigned __stdcall
ControlThread(LPVOID p)
{
	/* 
	* TODO - Enable the console resize logic.
	* With the current resize logic, we have two issues
	* 1) console screen buffer rows should be always 9999, irrespective of the user setting.
	* 2) when ssh client window is resized it clears everything and gives a blank screen.
	* For now we disable this logic.
	*
	* It looks to be a bug in our console hook event pty implementation.
	*/
	return 0;

	//short type, row, col;
	//DWORD len;
	//COORD coord;
	//SMALL_RECT rect;
	//while (1) {
	//	if (!ReadFile(pipe_ctrl, &type, 2, &len, NULL))
	//		break;
	//	if (type != PTY_SIGNAL_RESIZE_WINDOW)
	//		break;
	//	if (!ReadFile(pipe_ctrl, &col, 2, &len, NULL))
	//		break;
	//	if (!ReadFile(pipe_ctrl, &row, 2, &len, NULL))
	//		break;
	//	
	//	/* 
	//	 * when reducing width, console seemed to retain prior width 
	//	 * while increasing width, however, it behaves right
	//	 * 
	//	 * hence setting it less by 1 and setting it again to the right
	//	 * count
	//	 */
	//	
	//	coord.X = col - 1;
	//	coord.Y = row;
	//	rect.Top = 0;
	//	rect.Left = 0;
	//	rect.Bottom = row - 1;
	//	rect.Right = col - 2;
	//	SetConsoleScreenBufferSize(child_out, coord);
	//	SetConsoleWindowInfo(child_out, TRUE, &rect);

	//	coord.X = col;
	//	rect.Right = col - 1;
	//	SetConsoleScreenBufferSize(child_out, coord);
	//	SetConsoleWindowInfo(child_out, TRUE, &rect);
	//}
	//return 0;
}

DWORD 
ProcessEvent(void *p)
{
	wchar_t chUpdate;
	WORD  wAttributes;
	WORD  wX;
	WORD  wY;
	DWORD dwProcessId;
	DWORD wr = 0;
	DWORD event;
	HWND hwnd;
	LONG idObject;
	LONG idChild;
	CHAR_INFO pBuffer[MAX_EXPECTED_BUFFER_SIZE] = {0,};
	DWORD bufferSize;
	SMALL_RECT readRect;
	COORD coordBufSize;
	COORD coordBufCoord;

	if (!p)
		return ERROR_INVALID_PARAMETER;

	consoleEvent* current = (consoleEvent *)p;

	if (!current)
		return ERROR_INVALID_PARAMETER;

	event = current->event;
	hwnd = current->hwnd;
	idObject = current->idObject;
	idChild = current->idChild;

	if (event < EVENT_CONSOLE_CARET || event > EVENT_CONSOLE_LAYOUT)
		return ERROR_INVALID_PARAMETER;

	if (child_out == INVALID_HANDLE_VALUE || child_out == NULL)
		return ERROR_INVALID_PARAMETER;

	GetWindowThreadProcessId(hwnd, &dwProcessId);

	if (childProcessId != dwProcessId)
		return ERROR_SUCCESS;

	ZeroMemory(&consoleInfo, sizeof(consoleInfo));
	consoleInfo.cbSize = sizeof(consoleInfo);

	GetConsoleScreenBufferInfoEx(child_out, &consoleInfo);

	UINT viewPortHeight = consoleInfo.srWindow.Bottom - consoleInfo.srWindow.Top + 1;
	UINT viewPortWidth = consoleInfo.srWindow.Right - consoleInfo.srWindow.Left + 1;

	switch (event) {
	case EVENT_CONSOLE_CARET:
	{
		COORD co;
		co.X = LOWORD(idChild);
		co.Y = HIWORD(idChild);
		
		lastX = co.X;
		lastY = co.Y;

		if (lastX == 0 && lastY > currentLine)
			CalculateAndSetCursor(pipe_out, lastX, lastY, TRUE);
		else
			SendSetCursor(pipe_out, lastX + 1, lastY + 1);

		break;
	}
	case EVENT_CONSOLE_UPDATE_REGION:
	{
		readRect.Top = HIWORD(idObject);
		readRect.Left = LOWORD(idObject);
		readRect.Bottom = HIWORD(idChild);
		readRect.Right = LOWORD(idChild);

		readRect.Right = max(readRect.Right, ConSRWidth());

		/* Detect a "cls" (Windows) */
		if (!bStartup &&
		    (readRect.Top == consoleInfo.srWindow.Top || readRect.Top == nextConsoleInfo.srWindow.Top)) {
			BOOL isClearCommand = FALSE;
			isClearCommand = (consoleInfo.dwSize.X == readRect.Right + 1) && (consoleInfo.dwSize.Y == readRect.Bottom + 1);

			/* If cls then inform app to clear its buffers and return */
			if (isClearCommand) {
				SendClearScreen(pipe_out);
				ViewPortY = 0;
				lastViewPortY = 0;

				return ERROR_SUCCESS;
			}
		}

		/* Figure out the buffer size */		
		coordBufSize.Y = readRect.Bottom - readRect.Top + 1;
		coordBufSize.X = readRect.Right - readRect.Left + 1;

		/*
		 * Security check:  the maximum screen buffer size is 9999 columns x 9999 lines so check
		 * the computed buffer size for overflow.  since the X and Y in the COORD structure
		 * are shorts they could be negative.
		 */
		if (coordBufSize.X < 0 || coordBufSize.X > MAX_CONSOLE_COLUMNS ||
		    coordBufSize.Y < 0 || coordBufSize.Y > MAX_CONSOLE_ROWS)
			return ERROR_INVALID_PARAMETER;

		/* Compute buffer size */
		bufferSize = coordBufSize.X * coordBufSize.Y;
		if (bufferSize > MAX_EXPECTED_BUFFER_SIZE) {
			if (!bStartup) {
				SendClearScreen(pipe_out);
				ViewPortY = 0;
				lastViewPortY = 0;
			}
			return ERROR_SUCCESS;
		}
		
		/* The top left destination cell of the temporary buffer is row 0, col 0 */		
		coordBufCoord.X = 0;
		coordBufCoord.Y = 0;

		/* Copy the block from the screen buffer to the temp. buffer */
		if (!ReadConsoleOutput(child_out, pBuffer, coordBufSize, coordBufCoord, &readRect))
			return GetLastError();

		/* Set cursor location based on the reported location from the message */
		CalculateAndSetCursor(pipe_out, readRect.Left, readRect.Top, TRUE);

		/* Send the entire block */
		SendBuffer(pipe_out, pBuffer, bufferSize);
		lastViewPortY = ViewPortY;
		lastLineLength = readRect.Left;		
		
		break;
	}
	case EVENT_CONSOLE_UPDATE_SIMPLE:
	{
		chUpdate = LOWORD(idChild);
		wAttributes = HIWORD(idChild);
		wX = LOWORD(idObject);
		wY = HIWORD(idObject);
		
		readRect.Top = wY;
		readRect.Bottom = wY;
		readRect.Left = wX;
		readRect.Right = ConSRWidth();
		
		/* Set cursor location based on the reported location from the message */
		CalculateAndSetCursor(pipe_out, wX, wY, TRUE);
				
		coordBufSize.Y = readRect.Bottom - readRect.Top + 1;
		coordBufSize.X = readRect.Right - readRect.Left + 1;
		bufferSize = coordBufSize.X * coordBufSize.Y;

		/* The top left destination cell of the temporary buffer is row 0, col 0 */
		coordBufCoord.X = 0;
		coordBufCoord.Y = 0;

		/* Copy the block from the screen buffer to the temp. buffer */
		if (!ReadConsoleOutput(child_out, pBuffer, coordBufSize, coordBufCoord, &readRect))
			return GetLastError();

		SendBuffer(pipe_out, pBuffer, bufferSize);		

		break;
	}
	case EVENT_CONSOLE_UPDATE_SCROLL:
	{
		DWORD out = 0;
		LONG vd = idChild;
		LONG hd = idObject;
		LONG vn = abs(vd);

		if (vd > 0) {
			if (ViewPortY > 0)
				ViewPortY -= vn;
		} else {
			ViewPortY += vn;
		}

		break;
	}
	case EVENT_CONSOLE_LAYOUT:
	{
		if (consoleInfo.dwMaximumWindowSize.X == consoleInfo.dwSize.X &&
		    consoleInfo.dwMaximumWindowSize.Y == consoleInfo.dwSize.Y &&
		    (consoleInfo.dwCursorPosition.X == 0 && consoleInfo.dwCursorPosition.Y == 0)) {
			/* Screen has switched to fullscreen mode */
			SendClearScreen(pipe_out);
			savedViewPortY = ViewPortY;
			savedLastViewPortY = lastViewPortY;
			ViewPortY = 0;
			lastViewPortY = 0;;
			bFullScreen = TRUE;
		} else {
			/* Leave full screen mode if applicable */
			if (bFullScreen) {
				SendClearScreen(pipe_out);
				ViewPortY = savedViewPortY;
				lastViewPortY = savedLastViewPortY;
				bFullScreen = FALSE;
			}
		}
		break;
	}
	}

	return ERROR_SUCCESS;
}

unsigned __stdcall
ProcessEventQueue(LPVOID p)
{
	while (1) {
		while (head) {
			EnterCriticalSection(&criticalSection);
			consoleEvent* current = head;
			if (current) {
				if (current->next) {
					head = current->next;
					head->prior = NULL;
				} else {
					head = NULL;
					tail = NULL;
				}
			}

			LeaveCriticalSection(&criticalSection);
			if (current) {
				ProcessEvent(current);
				free(current);
			}
		}

		if (child_in != INVALID_HANDLE_VALUE && child_in != NULL &&
		    child_out != INVALID_HANDLE_VALUE && child_out != NULL) {
			ZeroMemory(&consoleInfo, sizeof(consoleInfo));
			consoleInfo.cbSize = sizeof(consoleInfo);

			/* This information is the live buffer that's currently in use */
			GetConsoleScreenBufferInfoEx(child_out, &consoleInfo);

			/* Set the cursor to the last known good location according to the live buffer */
			if (lastX != consoleInfo.dwCursorPosition.X ||
			    lastY != consoleInfo.dwCursorPosition.Y)
				SendSetCursor(pipe_out, consoleInfo.dwCursorPosition.X + 1, consoleInfo.dwCursorPosition.Y + 1);

			lastX = consoleInfo.dwCursorPosition.X;
			lastY = consoleInfo.dwCursorPosition.Y;
		}
		Sleep(100);
	}
	return 0;
}

void 
QueueEvent(DWORD event, HWND hwnd, LONG idObject, LONG idChild)
{
	consoleEvent* current = NULL;

	EnterCriticalSection(&criticalSection);
	current = malloc(sizeof(consoleEvent));
	if (current) {
		if (!head) {
			current->event = event;
			current->hwnd = hwnd;
			current->idChild = idChild;
			current->idObject = idObject;

			/* No links head == tail */
			current->next = NULL;
			current->prior = NULL;

			head = current;
			tail = current;
		} else {
			current->event = event;
			current->hwnd = hwnd;
			current->idChild = idChild;
			current->idObject = idObject;

			/* Current tail points to new tail */
			tail->next = current;

			/* New tail points to old tail */
			current->prior = tail;
			current->next = NULL;

			/* Update the tail pointer to the new last event */
			tail = current;
		}
	}
	LeaveCriticalSection(&criticalSection);
}

void FreeQueueEvent()
{
	EnterCriticalSection(&criticalSection);
	while (head) {
		consoleEvent* current = head;
		head = current->next;
		free(current);
	}
	head = NULL;
	tail = NULL;
	LeaveCriticalSection(&criticalSection);
}

unsigned __stdcall
ProcessPipes(LPVOID p)
{
	BOOL ret;
	DWORD dwStatus;
	char buf[128];

	/* process data from pipe_in and route appropriately */
	while (1) {
		ZeroMemory(buf, sizeof(buf));
		int rd = 0;

		GOTO_CLEANUP_ON_FALSE(ReadFile(pipe_in, buf, sizeof(buf) - 1, &rd, NULL)); /* read bufsize-1 */
		bStartup = FALSE;
		if(rd > 0)
			ProcessIncomingKeys(buf);
	}

cleanup:
	/* pipe_in has ended */
	PostThreadMessage(hostThreadId, WM_APPEXIT, 0, 0);
	dwStatus = GetLastError();
	return 0;
}

void CALLBACK 
ConsoleEventProc(HWINEVENTHOOK hWinEventHook,
    DWORD event,
    HWND hwnd,
    LONG idObject,
    LONG idChild,
    DWORD dwEventThread,
    DWORD dwmsEventTime)
{
	QueueEvent(event, hwnd, idObject, idChild);
}

void
ProcessMessages(void* p)
{
	DWORD dwStatus;
	SECURITY_ATTRIBUTES sa;
	MSG msg;

	sa.nLength = sizeof(SECURITY_ATTRIBUTES);
	sa.lpSecurityDescriptor = NULL;
	sa.bInheritHandle = TRUE;

	/* If we here then we are certain that we have a child process console, so we should be able to get child_in, child_out handles */
	while (child_in == (HANDLE)-1) {
		child_in = CreateFile(TEXT("CONIN$"), GENERIC_READ | GENERIC_WRITE,
					FILE_SHARE_WRITE | FILE_SHARE_READ,
					&sa, OPEN_EXISTING, 0, NULL);
	}

	while (child_out == (HANDLE)-1) {
		child_out = CreateFile(TEXT("CONOUT$"), GENERIC_READ | GENERIC_WRITE,
					FILE_SHARE_WRITE | FILE_SHARE_READ,
					&sa, OPEN_EXISTING, 0, NULL);
	}

	child_err = child_out;
	SizeWindow(child_out);
	/* Get the current buffer information after all the adjustments */
	GetConsoleScreenBufferInfoEx(child_out, &consoleInfo);
	/* Loop for the console output events */
	while (GetMessage(&msg, NULL, 0, 0)) {
		if (msg.message == WM_APPEXIT)
			break;
		else {
			TranslateMessage(&msg);
			DispatchMessage(&msg);
		}
	}

	/* cleanup */
	dwStatus = GetLastError();
	if (child_in != INVALID_HANDLE_VALUE)
		CloseHandle(child_in);
	if (child_out != INVALID_HANDLE_VALUE)
		CloseHandle(child_out);
}

int 
start_with_pty(wchar_t *command)
{
	STARTUPINFO si;
	PROCESS_INFORMATION pi;
	wchar_t *cmd = (wchar_t *)malloc(sizeof(wchar_t) * MAX_CMD_LEN);
	SECURITY_ATTRIBUTES sa;
	BOOL ret;
	DWORD dwStatus;
	HANDLE hEventHook = NULL;
	HMODULE hm_kernel32 = NULL, hm_user32 = NULL;
	wchar_t kernel32_dll_path[PATH_MAX]={0,}, user32_dll_path[PATH_MAX]={0,};

	if (cmd == NULL) {
		printf_s("ssh-shellhost is out of memory");
		exit(255);
	}

	if (!GetSystemDirectoryW(system32_path, PATH_MAX)) {
		printf_s("unable to retrieve system32 path\n");
		exit(255);
	}

	GOTO_CLEANUP_ON_ERR(wcsncpy_s(kernel32_dll_path, _countof(kernel32_dll_path), system32_path, wcsnlen(system32_path, _countof(system32_path)) + 1));
	GOTO_CLEANUP_ON_ERR(wcscat_s(kernel32_dll_path, _countof(kernel32_dll_path), L"\\kernel32.dll"));

	GOTO_CLEANUP_ON_ERR(wcsncpy_s(user32_dll_path, _countof(user32_dll_path), system32_path, wcsnlen(system32_path, _countof(system32_path)) + 1));
	GOTO_CLEANUP_ON_ERR(wcscat_s(user32_dll_path, _countof(user32_dll_path), L"\\user32.dll"));

	if ((hm_kernel32 = LoadLibraryW(kernel32_dll_path)) == NULL ||
	    (hm_user32 = LoadLibraryW(user32_dll_path)) == NULL ||
	    (__SetCurrentConsoleFontEx = (__t_SetCurrentConsoleFontEx)GetProcAddress(hm_kernel32, "SetCurrentConsoleFontEx")) == NULL ||
	    (__UnhookWinEvent = (__t_UnhookWinEvent)GetProcAddress(hm_user32, "UnhookWinEvent")) == NULL ||
	    (__SetWinEventHook = (__t_SetWinEventHook)GetProcAddress(hm_user32, "SetWinEventHook")) == NULL) {
		printf_s("cannot support a pseudo terminal. \n");
		return -1;
	}

	pipe_in = GetStdHandle(STD_INPUT_HANDLE);
	pipe_out = GetStdHandle(STD_OUTPUT_HANDLE);
	pipe_ctrl = GetStdHandle(STD_ERROR_HANDLE);

	/* copy pipe handles passed through std io*/
	if ((pipe_in == INVALID_HANDLE_VALUE) || (pipe_out == INVALID_HANDLE_VALUE) || (pipe_ctrl == INVALID_HANDLE_VALUE))
		return -1;

	cp = GetConsoleCP();

	/* 
	 * Windows PTY sends cursor positions in absolute coordinates starting from <0,0>
	 * We send a clear screen upfront to simplify client 
	 */	
	SendClearScreen(pipe_out);

	ZeroMemory(&inputSi, sizeof(STARTUPINFO));
	GetStartupInfo(&inputSi);
	memset(&sa, 0, sizeof(SECURITY_ATTRIBUTES));
	sa.bInheritHandle = TRUE;
	/* WM_APPEXIT */
	hostThreadId = GetCurrentThreadId();
	hostProcessId = GetCurrentProcessId();
	InitializeCriticalSection(&criticalSection);
	
	/* 
	 * Ignore the static code analysis warning C6387 
	 * as per msdn, third argument can be NULL when we specify WINEVENT_OUTOFCONTEXT
	 */
#pragma warning(suppress: 6387)
	hEventHook = __SetWinEventHook(EVENT_CONSOLE_CARET, EVENT_CONSOLE_END_APPLICATION, NULL,
					ConsoleEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);
	memset(&si, 0, sizeof(STARTUPINFO));
	memset(&pi, 0, sizeof(PROCESS_INFORMATION));
	/* Copy our parent buffer sizes */
	si.cb = sizeof(STARTUPINFO);
	si.dwFlags = 0;
	/* disable inheritance on pipe_in*/
	GOTO_CLEANUP_ON_FALSE(SetHandleInformation(pipe_in, HANDLE_FLAG_INHERIT, 0));
	
	/*
	* Launch via cmd.exe /c, otherwise known issues exist with color rendering in powershell
	*/
	_snwprintf_s(cmd, MAX_CMD_LEN, MAX_CMD_LEN, L"\"%ls\\cmd.exe\" /c \"%ls\"", system32_path, command);
	
	SetConsoleCtrlHandler(NULL, FALSE);
	GOTO_CLEANUP_ON_FALSE(CreateProcess(NULL, cmd, NULL, NULL, TRUE, CREATE_NEW_CONSOLE,
				NULL, NULL, &si, &pi));
	childProcessId = pi.dwProcessId;

	FreeConsole();
	Sleep(20);
	while (!AttachConsole(pi.dwProcessId)) {
		/* If user tries to execute a command (like dir) in pty session then we may run into this scenario. */
		if (GetExitCodeProcess(pi.hProcess, &child_exit_code) && child_exit_code != STILL_ACTIVE)
			goto cleanup;

		Sleep(100);
	}

	/* monitor child exist */
	child = pi.hProcess;
	monitor_thread = (HANDLE) _beginthreadex(NULL, 0, MonitorChild, NULL, 0, NULL);
	if (IS_INVALID_HANDLE(monitor_thread))
		goto cleanup;

	/* disable Ctrl+C hander in this process*/
	SetConsoleCtrlHandler(NULL, TRUE);
	
	initialize_keylen();

	io_thread = (HANDLE) _beginthreadex(NULL, 0, ProcessPipes, NULL, 0, NULL);
	if (IS_INVALID_HANDLE(io_thread))
		goto cleanup;

	ux_thread = (HANDLE) _beginthreadex(NULL, 0, ProcessEventQueue, NULL, 0, NULL);
	if (IS_INVALID_HANDLE(ux_thread))
		goto cleanup;

	ctrl_thread = (HANDLE)_beginthreadex(NULL, 0, ControlThread, NULL, 0, NULL);
	if (IS_INVALID_HANDLE(ctrl_thread))
		goto cleanup;

	ProcessMessages(NULL);
cleanup:
	dwStatus = GetLastError();
	if (child != INVALID_HANDLE_VALUE)
		TerminateProcess(child, 0);

	if (!IS_INVALID_HANDLE(monitor_thread)) {
		WaitForSingleObject(monitor_thread, INFINITE);
		CloseHandle(monitor_thread);
	}
	if (!IS_INVALID_HANDLE(ux_thread)) {
		TerminateThread(ux_thread, S_OK);
		CloseHandle(ux_thread);
	}
	if (!IS_INVALID_HANDLE(io_thread)) {
		TerminateThread(io_thread, 0);
		CloseHandle(io_thread);
	}

	if (!IS_INVALID_HANDLE(ctrl_thread)) {
		TerminateThread(ctrl_thread, 0);
		CloseHandle(ctrl_thread);
	}

	if (hEventHook)
		__UnhookWinEvent(hEventHook);
	
	FreeConsole();
	
	if (child != INVALID_HANDLE_VALUE) {
		CloseHandle(pi.hProcess);
		CloseHandle(pi.hThread);
	}
	
	FreeQueueEvent();
	DeleteCriticalSection(&criticalSection);
	
	if(cmd != NULL)
		free(cmd);

	return child_exit_code;
}

/* implements a basic shell - launches given cmd using CreateProcess */
int start_as_shell(wchar_t* cmd)
{
	STARTUPINFOW si;
	PROCESS_INFORMATION pi;

	memset(&si, 0, sizeof(STARTUPINFOW));
	memset(&pi, 0, sizeof(PROCESS_INFORMATION));
	si.cb = sizeof(STARTUPINFOW);

	if (CreateProcessW(NULL, cmd, NULL, NULL, TRUE, 0, NULL, NULL, &si, &pi) == FALSE) {
		printf("ssh-shellhost cannot run '%ls', error: %d", cmd, GetLastError());
		exit(255);
	}

	CloseHandle(pi.hThread);
	/* close std io handles */
	CloseHandle(GetStdHandle(STD_INPUT_HANDLE));
	CloseHandle(GetStdHandle(STD_OUTPUT_HANDLE));
	CloseHandle(GetStdHandle(STD_ERROR_HANDLE));
	child_exit_code = 255;

	/* wait for child to exit */
	WaitForSingleObject(pi.hProcess, INFINITE);

	if (!GetExitCodeProcess(pi.hProcess, &child_exit_code))
		printf("ssh-shellhost unable to track child process, error: %d", GetLastError());

	CloseHandle(pi.hProcess);
	return child_exit_code;
}

/*
 * Usage:
 * Execute commandline with PTY 
 *   ssh-shellhost.exe ---pty commandline
 * Note that in PTY mode, stderr is taken as the control channel
 * to receive Windows size change events
 *
 * Execute commandline like shell (plain IO redirection)
 * Syntax mimics cmd.exe -c usage. Note the explicit double quotes
 * around actual commandline to execute.
 *   ssh-shellhost.exe -c "commandline"
 * Ex.	ssh-shellhost.exe -c "notepad.exe file.txt"
 *	ssh-shellhost.exe -c ""my program.exe" "arg 1" "arg 2""
 */
int 
wmain(int ac, wchar_t **av)
{
	wchar_t *exec_command, *option, *cmdline;
	int with_pty, len;

	_set_invalid_parameter_handler(my_invalid_parameter_handler);

	if (ac == 1)
		goto usage;

	if ((cmdline = _wcsdup(GetCommandLineW())) == NULL) {
		printf("ssh-shellhost.exe ran out of memory");
		exit(255);
	}

	if (option = wcsstr(cmdline, L" ---pty "))
		with_pty = 1;
	else if (option = wcsstr(cmdline, L" -c "))
		with_pty = 0;
	else
		goto usage;

	if (with_pty)
		exec_command = option + wcslen(L" ---pty ");
	else
		exec_command = option + wcslen(L" -c ");

	/* strip preceding white spaces */
	while (*exec_command != L'\0' && *exec_command == L' ')
		exec_command++;

	if (*exec_command == L'\0')
		goto usage;

	if (with_pty)
		return start_with_pty(exec_command);
	else {
		/* if commandline is enclosed in double quotes, remove them */
		len = (int)wcslen(exec_command);
		if (len > 2 && *exec_command == L'\"' && *(exec_command + len - 1) == L'\"') {
			*(exec_command + len - 1) = L'\0';
			exec_command++;
		}
		return start_as_shell(exec_command);
	}
usage:
	printf("ssh-shellhost does not support command line: %ls", cmdline);
	exit(255);
}
