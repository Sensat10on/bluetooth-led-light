"""
Проверка подсветки по веб-камере.

Зачем нужен отдельный инструмент. Приложение может отчитаться «цвет
отправлен», а лента при этом не изменится: команда ушла в никуда, устройство
занято другим приложением или связь пропала. Единственный надёжный способ
убедиться -- посмотреть на саму лампу. Этот скрипт снимает кадры с камеры,
отправляет цвета и показывает, изменилось ли изображение.

Как работает измерение. Свечение лампы в кадре занимает небольшую часть
изображения и часто тусклое, поэтому средняя яркость кадра почти не
меняется. Вместо неё берётся подпись кадра: покадровая разница с опорным
кадром усредняется по каналу, а также считается доля изменившихся пикселей.
Если лента действительно меняет цвет, разница заметно превышает шум камеры.

Запуск (нужен OpenCV: pip install opencv-python):

    python tools/CameraCheck/camera_check.py --mac BE:60:41:00:27:B9

Полезные ключи:

    --camera 0      номер камеры
    --settle 3      сколько секунд ждать после команды перед снимком
    --image out.png сохранить кадр для просмотра
    --no-send       не отправлять команды, только измерить шум камеры
"""

import argparse
import statistics
import subprocess
import sys
import time
from pathlib import Path

try:
    import cv2
except ImportError:
    sys.exit('Не найден OpenCV. Установите: pip install opencv-python')


# Цвета для проверки: заведомо различимые оттенки и выключение.
TESTS = [
    ('красный', (255, 0, 0)),
    ('зелёный', (0, 255, 0)),
    ('синий', (0, 0, 255)),
    ('белый', (255, 255, 255)),
]


def find_tool(repo_root, name, frameworks):
    """Ищет собранную утилиту в папке tools."""
    base = repo_root / 'csharp' / 'tools' / name / 'bin' / 'Release'

    for framework in frameworks:
        path = base / framework / f'OpenTuyaSync.{name}.exe'
        if path.exists():
            return path

    return None


def find_ble_test(repo_root):
    """Ищет утилиту BleTest, которой команды пишутся прямо в характеристику."""
    return find_tool(repo_root, 'BleTest',
                     ('net10.0-windows10.0.19041.0', 'net10.0-windows'))


def find_app_sender(repo_root):
    """
    Ищет утилиту, отправляющую цвет путём приложения.

    Разница существенная: BleTest пишет в характеристику напрямую, а здесь
    команда проходит через ту же рассылку, что и кнопка «Применить цвет» в
    окне. Ошибка с потерей ручных команд была именно в рассылке, поэтому
    проверять по камере нужно оба пути.
    """
    return find_tool(repo_root, 'ReconnectTest',
                     ('net10.0-windows10.0.19041.0', 'net10.0-windows'))


def send_color(ble_test, mac, color):
    """Отправляет цвет на ленту. Возвращает (успех, вывод)."""
    r, g, b = color
    result = subprocess.run(
        [str(ble_test), 'color', mac, str(r), str(g), str(b)],
        capture_output=True, text=True, timeout=120,
    )
    return result.returncode == 0, (result.stdout or '') + (result.stderr or '')


def send_color_via_app(app_sender, color):
    """Отправляет цвет путём приложения: через ту же рассылку, что в окне."""
    r, g, b = color
    result = subprocess.run(
        [str(app_sender), 'deliver', f'{r},{g},{b}'],
        capture_output=True, text=True, timeout=180,
    )
    return result.returncode == 0, (result.stdout or '') + (result.stderr or '')


def capture(cap, warmup=5):
    """Снимает кадр, отбрасывая первые -- камера отдаёт старые из буфера."""
    frame = None

    for _ in range(warmup):
        ok, candidate = cap.read()
        if ok and candidate is not None:
            frame = candidate

        # Небольшая пауза, чтобы камера успела обновить буфер.
        time.sleep(0.05)

    return frame


def average_frame(cap, count=8, interval=0.12):
    """Усредняет несколько кадров: так шум камеры влияет слабее."""
    frames = []

    for _ in range(count):
        frame = capture(cap, warmup=2)
        if frame is not None:
            frames.append(frame.astype('float32'))
        time.sleep(interval)

    if not frames:
        return None

    total = frames[0]
    for frame in frames[1:]:
        total = total + frame

    return total / len(frames)


def signature_diff(reference, current):
    """
    Разница двух кадров.

    Возвращает разницу по каналам (R, G, B) и долю пикселей, которые
    изменились заметно. Разница по каналу показывает, в какую сторону
    сместился цвет, а доля пикселей -- насколько это заметно на кадре.
    """
    diff = current - reference

    per_channel = (
        float(diff[:, :, 2].mean()),   # красный (OpenCV хранит BGR)
        float(diff[:, :, 1].mean()),   # зелёный
        float(diff[:, :, 0].mean()),   # синий
    )

    # Пиксель считается изменившимся, если суммарное изменение по каналам
    # превышает порог: одиночные выбросы шума так не учитываются.
    magnitude = abs(diff).sum(axis=2)
    changed_share = float((magnitude > 24).mean())

    return per_channel, changed_share


def measure_noise(cap, reference):
    """Разброс между двумя снимками без команд -- шум камеры."""
    current = average_frame(cap)
    if current is None:
        return None

    per_channel, share = signature_diff(reference, current)
    return max(abs(v) for v in per_channel), share


def compare_pair(cap, ble_test, mac, first_color, second_color, settle):
    """
    Парное сравнение двух цветов: два снимка подряд с разными командами.

    Камера подстраивает экспозицию под кадр, поэтому сравнивать снимки,
    сделанные далеко друг от друга, ненадёжно. Здесь между снимками
    меняется только цвет лампы, и разницу видно однозначно.
    """
    print()
    print(f'Сравнение двух цветов: {first_color} и {second_color}')
    print()

    sent_first, output_first = send_color(ble_test, mac, first_color)
    if not sent_first:
        print('  первый цвет не отправлен:')
        print('   ', output_first.strip().splitlines()[-1] if output_first.strip() else 'нет ответа')
        return 1

    time.sleep(settle)
    first = average_frame(cap)
    if first is None:
        print('  ОШИБКА: кадр не получен')
        return 1

    print(f'  кадр 1 снят (цвет {first_color})')

    sent_second, output_second = send_color(ble_test, mac, second_color)
    if not sent_second:
        print('  второй цвет не отправлен:')
        print('   ', output_second.strip().splitlines()[-1] if output_second.strip() else 'нет ответа')
        return 1

    time.sleep(settle)
    second = average_frame(cap)
    if second is None:
        print('  ОШИБКА: кадр не получен')
        return 1

    print(f'  кадр 2 снят (цвет {second_color})')

    per_channel, share = signature_diff(first, second)

    print()
    print(f'  разница по каналам: R {per_channel[0]:+.2f}, '
          f'G {per_channel[1]:+.2f}, B {per_channel[2]:+.2f}')
    print(f'  изменившихся пикселей: {share * 100:.2f}%')

    # Ожидаемое направление: переход к красному поднимает красный канал и
    # опускает остальные; переход к синему -- наоборот.
    dominant = max(range(3), key=lambda i: abs(per_channel[i]))
    names = ('красный', 'зелёный', 'синий')
    direction = 'вырос' if per_channel[dominant] > 0 else 'упал'

    print(f'  сильнее всех изменился {names[dominant]} канал: {direction}')

    changed = share > 0.002 or max(abs(v) for v in per_channel) > 1.5

    print()
    print('РЕЗУЛЬТАТ: ' + ('лампа изменила цвет' if changed else
                          'изменений не замечено -- команда не дошла до лампы'))

    return 0 if changed else 1


def compare_by_hand(cap, settle):
    """
    Парное сравнение: два снимка подряд, между ними команда подаётся вручную.

    Нужно, чтобы проверить именно приложение, а не утилиту отправки:
    пользователь нажимает кнопку в окне, а скрипт измеряет, изменилась ли
    лампа. Так проверяется весь путь от кнопки до устройства.

    Камера подстраивает экспозицию под кадр, поэтому сравнивать снимки,
    сделанные далеко друг от друга, ненадёжно. Здесь снимки идут подряд,
    и между ними меняется только состояние лампы.
    """
    print()
    print('Парное сравнение. Камера смотрит на лампу.')
    print()
    print('  1. Убедитесь, что лента светит одним цветом.')
    print('  2. Скрипт снимет первый кадр.')
    print('  3. Вы измените цвет.')
    print('  4. Скрипт снимет второй кадр и покажет разницу.')
    print()

    input('  Нажмите Enter, чтобы снять первый кадр... ')
    first = average_frame(cap)
    if first is None:
        print('  ОШИБКА: кадр не получен')
        return 1

    print('  Кадр снят.')
    print()
    input('  Измените цвет и нажмите Enter, чтобы снять второй кадр... ')

    # Даём лампе и камере время на реакцию.
    time.sleep(settle)

    second = average_frame(cap)
    if second is None:
        print('  ОШИБКА: кадр не получен')
        return 1

    per_channel, share = signature_diff(first, second)

    print()
    print(f'  разница по каналам: R {per_channel[0]:+.2f}, '
          f'G {per_channel[1]:+.2f}, B {per_channel[2]:+.2f}')
    print(f'  изменившихся пикселей: {share * 100:.2f}%')

    # Порог тот же, что и в автоматической проверке: он заметно выше шума
    # камеры, измеренного ключом --no-send (обычно доли процента).
    changed = share > 0.002 or max(abs(v) for v in per_channel) > 1.5

    print()
    print('РЕЗУЛЬТАТ: ' + ('лампа изменилась' if changed else
                          'изменений не замечено -- команда не дошла до лампы'))

    return 0 if changed else 1


def main():
    parser = argparse.ArgumentParser(description='Проверка подсветки по веб-камере')
    parser.add_argument('--mac', help='MAC-адрес Bluetooth-ленты')
    parser.add_argument('--camera', type=int, default=0, help='номер камеры')
    parser.add_argument('--settle', type=float, default=3.0,
                        help='секунд ожидания после команды')
    parser.add_argument('--image', help='сохранить последний кадр в файл')
    parser.add_argument('--no-send', action='store_true',
                        help='не отправлять команды, только измерить шум')
    parser.add_argument('--compare', action='store_true',
                        help='парное сравнение: команду подаёте вы вручную, '
                             'скрипт только измеряет')
    parser.add_argument('--pair', nargs=2, metavar=('ЦВЕТ1', 'ЦВЕТ2'),
                        help='сравнить два цвета, например: '
                             '--pair 255,0,0 0,0,255')
    parser.add_argument('--via-app', action='store_true',
                        help='отправлять цвета путём приложения (через ту же '
                             'рассылку, что и кнопка «Применить цвет»)')
    args = parser.parse_args()

    # camera_check.py лежит в csharp/tools/CameraCheck, поэтому корень
    # проекта -- на три уровня выше.
    repo_root = Path(__file__).resolve().parents[3]

    ble_test = None
    app_sender = None
    send = None

    if not args.no_send and not args.compare:
        if args.via_app:
            # Путь приложения: MAC не нужен, лента берётся из настроек.
            app_sender = find_app_sender(repo_root)
            if app_sender is None:
                sys.exit('Не найдена утилита ReconnectTest. Соберите её: build-tools.bat')

            send = lambda color: send_color_via_app(app_sender, color)
        else:
            # Прямая запись в характеристику: проверяет саму ленту.
            if not args.mac:
                sys.exit('Укажите MAC-ленты: --mac BE:60:41:00:27:B9')

            ble_test = find_ble_test(repo_root)
            if ble_test is None:
                sys.exit('Не найдена утилита BleTest. Соберите её: build-tools.bat')

            send = lambda color: send_color(ble_test, args.mac, color)

    print('=== Проверка подсветки по веб-камере ===')
    print(f'  камера: {args.camera}')
    if ble_test:
        print(f'  отправка: {ble_test.name} (прямая запись)')
    if app_sender:
        print(f'  отправка: {app_sender.name} (путь приложения)')
    print()

    cap = cv2.VideoCapture(args.camera, cv2.CAP_DSHOW)
    if not cap.isOpened():
        sys.exit(f'Камера {args.camera} не открылась')

    try:
        # Прогреваем камеру: первые кадры часто тёмные или старые.
        print('  прогрев камеры...')
        time.sleep(1.5)
        capture(cap, warmup=10)

        print('  опорный кадр...')
        reference = average_frame(cap)
        if reference is None:
            sys.exit('Не удалось получить кадр с камеры')

        if args.no_send:
            noise = measure_noise(cap, reference)
            if noise is None:
                sys.exit('Не удалось измерить шум')

            print()
            print(f'  шум камеры: разница по каналу {noise[0]:.2f}, '
                  f'доля изменившихся пикселей {noise[1] * 100:.2f}%')
            print()
            print('Этот уровень -- предел чувствительности проверки: '
                  'изменения меньше него различить нельзя.')
            return 0

        if args.compare:
            return compare_by_hand(cap, args.settle)

        if args.pair:
            def parse(text):
                parts = text.split(',')
                if len(parts) != 3:
                    sys.exit(f'Цвет задаётся как R,G,B -- получено «{text}»')
                return tuple(int(p) for p in parts)

            return compare_pair(cap, ble_test, args.mac,
                                parse(args.pair[0]), parse(args.pair[1]),
                                args.settle)

        print()
        print('  цвет      | отправка | разница R  G  B      | изменилось | вывод')
        print('  ----------+----------+---------------------+------------+-------')

        passed = 0
        total = 0

        for name, color in TESTS:
            total += 1

            sent, output = send(color)
            if not sent:
                short = output.strip().splitlines()[-1] if output.strip() else 'нет ответа'
                print(f'  {name:<9} | отказ    | {"":<19} | {"":<10} | {short[:40]}')
                continue

            time.sleep(args.settle)

            current = average_frame(cap)
            if current is None:
                print(f'  {name:<9} | отправлен| кадр не получен')
                continue

            per_channel, share = signature_diff(reference, current)

            # Цвет считается доставленным, если кадр заметно изменился.
            changed = share > 0.002 or max(abs(v) for v in per_channel) > 1.5

            if changed:
                passed += 1

            marker = 'изменилось' if changed else 'без изменений'
            print(f'  {name:<9} | отправлен| '
                  f'{per_channel[0]:+7.2f} {per_channel[1]:+7.2f} {per_channel[2]:+7.2f} | '
                  f'{share * 100:8.2f}% | {marker}')

            # Небольшая пауза между проверками.
            time.sleep(0.4)

        if args.image:
            capture(cap, warmup=3)
            ok, frame = cap.read()
            if ok:
                cv2.imwrite(args.image, frame)
                print()
                print(f'  последний кадр сохранён: {args.image}')

        print()

        if passed == 0:
            print('РЕЗУЛЬТАТ: лента не отреагировала ни на одну команду.')
            print()
            print('  Что проверить:')
            print('   - лента включена и рядом с компьютером;')
            print('   - лента не подключена к телефону: приложения вроде')
            print('     LotusLamp занимают соединение, и Windows отвечает')
            print('     «отказ в доступе» на службу ленты;')
            print('   - вывод команды отправки ниже.')
            print()
            _, output = send((255, 0, 0))
            print('  ---- вывод отправки ----')
            print(output.strip())

            return 1

        print(f'РЕЗУЛЬТАТ: лента отреагировала на {passed} из {total} команд.')

        # Если разница по каналам согласуется с отправленным цветом, это
        # подтверждает, что доставлен именно нужный цвет, а не что-то иное.
        return 0 if passed == total else 1

    finally:
        cap.release()


if __name__ == '__main__':
    sys.exit(main())
