#!/usr/bin/env python3
"""
测试脚本：验证 DirectTextInserter 功能

此脚本用于测试新的文本插入功能，验证：
1. DirectTextInserter 是否正确初始化
2. 文本插入是否成功
3. 剪贴板是否保持不变
4. 资源清理是否正确
5. 线程安全性

使用方法：
1. 在 macOS 上运行此脚本
2. 在终端中运行：python test_text_inserter.py
3. 脚本会提示你在 5 秒内切换到一个文本编辑器
4. 观察文本是否正确插入
5. 检查剪贴板内容是否保持不变
"""
import sys
import time
import threading
import logging

# 配置日志
logging.basicConfig(
    level=logging.DEBUG,
    format='%(asctime)s - %(name)s - %(levelname)s - %(message)s'
)

logger = logging.getLogger(__name__)


def test_initialization():
    """测试 DirectTextInserter 初始化"""
    print("=" * 60)
    print("测试 1: DirectTextInserter 初始化")
    print("=" * 60)

    try:
        from text_inserter import DirectTextInserter

        inserter = DirectTextInserter()
        print("✅ DirectTextInserter 初始化成功")
        return inserter
    except Exception as e:
        print(f"❌ DirectTextInserter 初始化失败: {e}")
        print("回退到 TextInserter（剪贴板方案）")
        from text_inserter import TextInserter
        inserter = TextInserter()
        return inserter


def test_text_insertion(inserter, test_text):
    """测试文本插入"""
    print("\n" + "=" * 60)
    print("测试 2: 文本插入")
    print("=" * 60)

    print(f"测试文本: {test_text}")
    print(f"文本长度: {len(test_text)} 字符")

    try:
        # 提示用户切换到文本编辑器
        print("\n请在 5 秒内切换到一个文本编辑器（如 TextEdit、VS Code 等）")
        print("并将光标放在要插入文本的位置...")

        for i in range(5, 0, -1):
            print(f"{i}...", flush=True)
            time.sleep(1)

        print("\n开始插入文本...")
        inserter.insert(test_text)
        print("✅ 文本插入完成")

        return True
    except Exception as e:
        print(f"❌ 文本插入失败: {e}")
        import traceback
        traceback.print_exc()
        return False


def test_clipboard_preservation(test_text):
    """测试剪贴板是否保持不变"""
    print("\n" + "=" * 60)
    print("测试 3: 剪贴板保持验证")
    print("=" * 60)

    import subprocess

    # 设置测试文本到剪贴板
    original_clipboard = "这是剪贴板原始内容 - 测试"
    print(f"设置剪贴板为: {original_clipboard}")

    process = subprocess.Popen(
        ['pbcopy'],
        stdin=subprocess.PIPE,
        env={'LANG': 'en_US.UTF-8'}
    )
    process.communicate(input=original_clipboard.encode('utf-8'))
    process.wait()

    # 提示用户
    print("\n请在 5 秒内切换到一个文本编辑器...")
    for i in range(5, 0, -1):
        print(f"{i}...", flush=True)
        time.sleep(1)

    # 插入文本
    from text_inserter import insert_text
    print("\n插入文本...")
    insert_text(test_text)

    # 读取剪贴板内容
    print("\n检查剪贴板内容...")
    time.sleep(0.5)  # 等待剪贴板稳定

    process = subprocess.Popen(
        ['pbpaste'],
        stdout=subprocess.PIPE,
        env={'LANG': 'en_US.UTF-8'}
    )
    output, _ = process.communicate()
    current_clipboard = output.decode('utf-8')

    print(f"原始剪贴板: {original_clipboard}")
    print(f"当前剪贴板: {current_clipboard}")

    if current_clipboard == original_clipboard:
        print("✅ 剪贴板保持不变（DirectTextInserter 工作正常）")
        return True
    else:
        print("❌ 剪贴板被修改（可能使用了 TextInserter 回退方案）")
        return False


def test_resource_cleanup():
    """测试资源清理"""
    print("\n" + "=" * 60)
    print("测试 4: 资源清理")
    print("=" * 60)

    try:
        from text_inserter import DirectTextInserter

        # 创建多个实例
        inserters = []
        for i in range(3):
            inserter = DirectTextInserter()
            inserters.append(inserter)
            print(f"创建实例 {i+1}/3")

        # 清理所有实例
        for i, inserter in enumerate(inserters):
            inserter.close()
            print(f"清理实例 {i+1}/3")

        print("✅ 资源清理测试完成")
        return True
    except Exception as e:
        print(f"❌ 资源清理测试失败: {e}")
        import traceback
        traceback.print_exc()
        return False


def test_context_manager():
    """测试上下文管理器协议"""
    print("\n" + "=" * 60)
    print("测试 5: 上下文管理器")
    print("=" * 60)

    try:
        from text_inserter import DirectTextInserter

        # 测试 with 语句
        with DirectTextInserter() as inserter:
            print("在上下文管理器中创建插入器")
            # 插入器应该正常工作
            pass

        print("✅ 上下文管理器测试完成（资源已自动清理）")
        return True
    except Exception as e:
        print(f"❌ 上下文管理器测试失败: {e}")
        import traceback
        traceback.print_exc()
        return False


def test_thread_safety():
    """测试线程安全性"""
    print("\n" + "=" * 60)
    print("测试 6: 线程安全")
    print("=" * 60)

    try:
        from text_inserter import insert_text

        results = []
        errors = []

        def worker(thread_id):
            """工作线程函数"""
            try:
                # 每个线程调用 insert_text
                insert_text(f"线程{thread_id}")
                results.append(thread_id)
            except Exception as e:
                errors.append((thread_id, e))

        # 创建多个线程
        threads = []
        num_threads = 5

        print(f"启动 {num_threads} 个线程...")
        for i in range(num_threads):
            t = threading.Thread(target=worker, args=(i,))
            threads.append(t)
            t.start()

        # 等待所有线程完成
        for t in threads:
            t.join()

        if len(results) == num_threads and len(errors) == 0:
            print(f"✅ 所有 {num_threads} 个线程成功完成")
            return True
        else:
            print(f"❌ 部分线程失败: {len(results)}/{num_threads} 成功")
            if errors:
                print(f"错误: {errors}")
            return False

    except Exception as e:
        print(f"❌ 线程安全测试失败: {e}")
        import traceback
        traceback.print_exc()
        return False


def test_batch_performance():
    """测试批量性能"""
    print("\n" + "=" * 60)
    print("测试 7: 批量性能")
    print("=" * 60)

    try:
        from text_inserter import DirectTextInserter

        inserter = DirectTextInserter()

        # 测试不同长度的文本
        test_cases = [
            (10, "短文本"),
            (50, "中文本"),
            (100, "长文本"),
            (200, "超长文本"),
        ]

        for length, description in test_cases:
            # 生成测试文本
            test_text = "A" * length

            # 测量时间
            start = time.time()
            inserter.insert(test_text)
            elapsed = time.time() - start

            print(f"{description} ({length} 字符): {elapsed*1000:.2f}ms")

        print("✅ 批量性能测试完成")
        return True

    except Exception as e:
        print(f"❌ 批量性能测试失败: {e}")
        import traceback
        traceback.print_exc()
        return False


def test_special_characters():
    """测试特殊字符"""
    print("\n" + "=" * 60)
    print("测试 8: 特殊字符")
    print("=" * 60)

    from text_inserter import DirectTextInserter

    test_cases = [
        ("Hello\nWorld", "换行符"),
        ("Hello\tWorld", "制表符"),
        ("Hello 👋 World", "Emoji"),
        ("Hello 你好 World", "中文混合"),
        ("@#$%^&*()", "特殊符号"),
    ]

    results = []

    for test_text, description in test_cases:
        try:
            print(f"\n测试: {description}")
            print(f"文本: {repr(test_text)}")

            inserter = DirectTextInserter()

            print(f"请在 5 秒内切换到文本编辑器...")
            for i in range(5, 0, -1):
                print(f"{i}...", flush=True)
                time.sleep(1)

            inserter.insert(test_text)
            print(f"✅ {description} 插入成功")

            inserter.close()
            results.append((description, True))

            time.sleep(1)

        except Exception as e:
            print(f"❌ {description} 插入失败: {e}")
            results.append((description, False))

    success_count = sum(1 for _, success in results if success)
    print(f"\n特殊字符测试: {success_count}/{len(results)} 成功")

    return success_count == len(results)


def main():
    """主测试函数"""
    print("\n" + "=" * 60)
    print("VoiceTyper 文本插入测试套件")
    print("=" * 60)

    # 检查平台
    import platform
    if platform.system() != 'Darwin':
        print("❌ 此测试脚本只能在 macOS 上运行")
        sys.exit(1)

    print(f"平台: {platform.system()} {platform.mac_ver()[0]}")
    print(f"Python: {sys.version}")

    # 测试用例
    test_cases = [
        ("Hello World!", "英文测试"),
        ("你好世界", "中文测试"),
        ("Hello 你好 World 🌍", "混合测试（含 emoji）"),
        ("@#$%^&*()", "特殊符号测试"),
    ]

    # 测试初始化
    inserter = test_initialization()

    if inserter is None:
        print("\n❌ 无法初始化文本插入器，测试终止")
        sys.exit(1)

    # 询问用户是否继续
    print("\n是否继续进行交互式测试？(y/n)")
    choice = input().strip().lower()

    if choice != 'y':
        print("\n跳过交互式测试")
        print("\n提示：你可以在使用 VoiceTyper 时观察：")
        print("1. 语音输入后，文本是否正确插入")
        print("2. 原有的剪贴板内容是否保持不变")
        print("\n如果两者都正常，说明 DirectTextInserter 工作正常 ✅")

        # 运行非交互式测试
        print("\n运行非交互式测试...")
        non_interactive_tests = [
            ("资源清理", test_resource_cleanup),
            ("上下文管理器", test_context_manager),
            ("线程安全", test_thread_safety),
            ("批量性能", test_batch_performance),
        ]

        results = []
        for name, test_func in non_interactive_tests:
            print(f"\n运行: {name}")
            try:
                success = test_func()
                results.append((name, success))
            except Exception as e:
                print(f"测试异常: {e}")
                results.append((name, False))

        # 总结
        print("\n" + "=" * 60)
        print("非交互式测试总结")
        print("=" * 60)

        for name, success in results:
            status = "✅ 通过" if success else "❌ 失败"
            print(f"{name}: {status}")

        sys.exit(0)

    # 执行交互式测试
    results = []

    for test_text, description in test_cases:
        print(f"\n{'=' * 60}")
        print(f"测试: {description}")
        print(f"{'=' * 60}")

        success = test_text_insertion(inserter, test_text)
        results.append((description, success))

        print(f"\n{description}: {'✅ 通过' if success else '❌ 失败'}")
        time.sleep(2)

    # 剪贴板保持测试（只在 DirectTextInserter 下执行）
    if isinstance(inserter, object).__name__ != 'TextInserter':
        print("\n是否测试剪贴板保持功能？(y/n)")
        choice = input().strip().lower()

        if choice == 'y':
            success = test_clipboard_preservation(test_cases[0][0])
            results.append(("剪贴板保持", success))

    # 特殊字符测试
    print("\n是否测试特殊字符？(y/n)")
    choice = input().strip().lower()

    if choice == 'y':
        success = test_special_characters()
        results.append(("特殊字符", success))

    # 总结
    print("\n" + "=" * 60)
    print("测试总结")
    print("=" * 60)

    for description, success in results:
        status = "✅ 通过" if success else "❌ 失败"
        print(f"{description}: {status}")

    passed = sum(1 for _, success in results if success)
    total = len(results)

    print(f"\n总计: {passed}/{total} 测试通过")

    if passed == total:
        print("\n🎉 所有测试通过！")
        sys.exit(0)
    else:
        print(f"\n⚠️  有 {total - passed} 个测试失败")
        sys.exit(1)


if __name__ == '__main__':
    main()
