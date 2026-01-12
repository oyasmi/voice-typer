package input

// Inserter 文本插入接口
type Inserter interface {
	// Insert 插入文本到当前光标位置
	Insert(text string) error
}
