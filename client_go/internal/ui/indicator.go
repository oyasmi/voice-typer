package ui

import (
	"runtime"
	"sync"
	"syscall"
	"unsafe"
)

var (
	user32 = syscall.NewLazyDLL("user32.dll")
	gdi32  = syscall.NewLazyDLL("gdi32.dll")

	procRegisterClassExW    = user32.NewProc("RegisterClassExW")
	procCreateWindowExW     = user32.NewProc("CreateWindowExW")
	procShowWindow          = user32.NewProc("ShowWindow")
	procUpdateWindow        = user32.NewProc("UpdateWindow")
	procDestroyWindow       = user32.NewProc("DestroyWindow")
	procDefWindowProcW      = user32.NewProc("DefWindowProcW")
	procGetMessageW         = user32.NewProc("GetMessageW")
	procTranslateMessage    = user32.NewProc("TranslateMessage")
	procDispatchMessageW    = user32.NewProc("DispatchMessageW")
	procPostQuitMessage     = user32.NewProc("PostQuitMessage")
	procSetWindowPos        = user32.NewProc("SetWindowPos")
	procMessageBoxW         = user32.NewProc("MessageBoxW")
	procGetSystemMetrics    = user32.NewProc("GetSystemMetrics")
	procInvalidateRect      = user32.NewProc("InvalidateRect")
	procBeginPaint          = user32.NewProc("BeginPaint")
	procEndPaint            = user32.NewProc("EndPaint")
	procDrawTextW           = user32.NewProc("DrawTextW")
	procSetTextColor        = gdi32.NewProc("SetTextColor")
	procSetBkMode           = gdi32.NewProc("SetBkMode")
	procCreateSolidBrush    = gdi32.NewProc("CreateSolidBrush")
	procFillRect            = user32.NewProc("FillRect")
	procDeleteObject        = gdi32.NewProc("DeleteObject")
)

const (
	WS_EX_TOPMOST     = 0x00000008
	WS_EX_TOOLWINDOW  = 0x00000080
	WS_EX_LAYERED     = 0x00080000
	WS_POPUP          = 0x80000000
	WS_VISIBLE        = 0x10000000
	
	SW_SHOW           = 5
	SW_HIDE           = 0
	
	WM_DESTROY        = 0x0002
	WM_PAINT          = 0x000F
	WM_CLOSE          = 0x0010
	
	SM_CXSCREEN       = 0
	SM_CYSCREEN       = 1
	
	DT_CENTER         = 0x1
	DT_VCENTER        = 0x4
	DT_SINGLELINE     = 0x20
	
	TRANSPARENT       = 1
)

type WNDCLASSEX struct {
	Size       uint32
	Style      uint32
	WndProc    uintptr
	ClsExtra   int32
	WndExtra   int32
	Instance   syscall.Handle
	Icon       syscall.Handle
	Cursor     syscall.Handle
	Background syscall.Handle
	MenuName   *uint16
	ClassName  *uint16
	IconSm     syscall.Handle
}

type RECT struct {
	Left, Top, Right, Bottom int32
}

type PAINTSTRUCT struct {
	Hdc         syscall.Handle
	Erase       int32
	RcPaint     RECT
	Restore     int32
	IncUpdate   int32
	RgbReserved [32]byte
}

// Indicator 录音提示窗口
type Indicator struct {
	hwnd      syscall.Handle
	status    string
	mutex     sync.Mutex
	className string
	width     int
	height    int
}

var globalIndicator *Indicator

// NewIndicator 创建提示窗口
func NewIndicator(width, height int, opacity float64) *Indicator {
	ind := &Indicator{
		width:     width,
		height:    height,
		className: "VoiceTyperIndicator",
		status:    "🎤 Recording...",
	}
	globalIndicator = ind
	
	go ind.runLoop()
	
	return ind
}

func (i *Indicator) runLoop() {
	runtime.LockOSThread()
	
	className, _ := syscall.UTF16PtrFromString(i.className)
	
	// Register Class
	wc := WNDCLASSEX{
		Size:      uint32(unsafe.Sizeof(WNDCLASSEX{})),
		Style:     0,
		WndProc:   syscall.NewCallback(wndProc),
		Instance:  0,
		Background: 0, // No background brush, handled in PAINT
		ClassName: className,
	}
	
	procRegisterClassExW.Call(uintptr(unsafe.Pointer(&wc)))
	
	// Calculate position (Top Center)
	screenWidth, _, _ := procGetSystemMetrics.Call(SM_CXSCREEN)
	x := (int(screenWidth) - i.width) / 2
	y := 50 // Top margin
	
	// Create Window
	hwnd, _, _ := procCreateWindowExW.Call(
		WS_EX_TOPMOST | WS_EX_TOOLWINDOW,
		uintptr(unsafe.Pointer(className)),
		0,
		WS_POPUP, // No border
		uintptr(x), uintptr(y),
		uintptr(i.width), uintptr(i.height),
		0, 0, 0, 0,
	)
	
	i.hwnd = syscall.Handle(hwnd)
	
	// Message Loop
	var msg struct {
		Hwnd    syscall.Handle
		Message uint32
		WParam  uintptr
		LParam  uintptr
		Time    uint32
		Pt      struct{ X, Y int32 }
	}
	
	for {
		ret, _, _ := procGetMessageW.Call(uintptr(unsafe.Pointer(&msg)), 0, 0, 0)
		if ret == 0 {
			break
		}
		procTranslateMessage.Call(uintptr(unsafe.Pointer(&msg)))
		procDispatchMessageW.Call(uintptr(unsafe.Pointer(&msg)))
	}
}

func wndProc(hwnd syscall.Handle, msg uint32, wparam, lparam uintptr) uintptr {
	switch msg {
	case WM_PAINT:
		var ps PAINTSTRUCT
		hdc, _, _ := procBeginPaint.Call(uintptr(hwnd), uintptr(unsafe.Pointer(&ps)))
		
		// Fill Background (Dark)
		rect := ps.RcPaint
		brush, _, _ := procCreateSolidBrush.Call(0x333333) // Dark Grey 0x333333 (BGR?) - 0x333333 is grey
		procFillRect.Call(hdc, uintptr(unsafe.Pointer(&rect)), brush)
		procDeleteObject.Call(brush)
		
		// Draw Text
		procSetBkMode.Call(hdc, TRANSPARENT)
		procSetTextColor.Call(hdc, 0xFFFFFF) // White
		
		text := "..."
		if globalIndicator != nil {
			globalIndicator.mutex.Lock()
			text = globalIndicator.status
			globalIndicator.mutex.Unlock()
		}
		
		textPtr, _ := syscall.UTF16PtrFromString(text)
		procDrawTextW.Call(
			hdc,
			uintptr(unsafe.Pointer(textPtr)),
			uintptr(0xFFFFFFFF), // -1: Null terminated string
			uintptr(unsafe.Pointer(&rect)),
			DT_CENTER | DT_VCENTER | DT_SINGLELINE,
		)
		
		procEndPaint.Call(uintptr(hwnd), uintptr(unsafe.Pointer(&ps)))
		return 0
		
	case WM_DESTROY:
		procPostQuitMessage.Call(0)
		return 0
	}
	
	ret, _, _ := procDefWindowProcW.Call(uintptr(hwnd), uintptr(msg), wparam, lparam)
	return ret
}

func (i *Indicator) Show() {
	if i.hwnd != 0 {
		procShowWindow.Call(uintptr(i.hwnd), SW_SHOW)
		procUpdateWindow.Call(uintptr(i.hwnd))
	}
}

func (i *Indicator) Hide() {
	if i.hwnd != 0 {
		procShowWindow.Call(uintptr(i.hwnd), SW_HIDE)
	}
}

func (i *Indicator) SetStatus(status string) {
	i.mutex.Lock()
	i.status = status
	i.mutex.Unlock()
	
	if i.hwnd != 0 {
		// Queue repaint
		procInvalidateRect.Call(uintptr(i.hwnd), 0, 1)
	}
}

func (i *Indicator) Close() {
	if i.hwnd != 0 {
		procDestroyWindow.Call(uintptr(i.hwnd))
	}
}

// Helper for MessageBox
func ShowMessageBox(title, message string) {
	t, _ := syscall.UTF16PtrFromString(title)
	m, _ := syscall.UTF16PtrFromString(message)
	procMessageBoxW.Call(0, uintptr(unsafe.Pointer(m)), uintptr(unsafe.Pointer(t)), 0)
}
