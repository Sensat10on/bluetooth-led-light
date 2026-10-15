"""
Создаёт иконку приложения из готового изображения.

Исходник -- assets/icon_source.jpg: монитор с радужной подсветкой по краям и
знаком Bluetooth на экране. Рисовать это заново смысла нет: в исходнике уже
есть объём, стеклянный отблеск экрана и мягкое цветное свечение.

Что делает скрипт:

  * убирает фон -- он залит чёрным, поэтому прозрачность берётся из яркости:
    свечение по краям остаётся, чёрный фон исчезает;
  * делает монитор непрозрачным изнутри, включая тёмную рамку: иначе
    полупрозрачные пиксели просвечивают фоном и иконка выглядит выцветшей;
  * обрезает служебную подпись внизу исходника;
  * вписывает изображение в квадрат и уменьшает до нужных размеров.

Требуется Pillow. Запуск: python csharp/assets/make_icon.py
"""

from pathlib import Path

from PIL import Image, ImageFilter

# Исходник в большом разрешении: при уменьшении это даёт чистые края.
SOURCE = 'icon_source.jpg'

# Размеры внутри .ico. Мелкие нужны для списка файлов и панели задач,
# крупные -- для плиток и окна свойств.
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]

# Ниже этой строки исходника начинается служебная подпись -- её в иконке
# быть не должно.
WATERMARK_TOP = 1930

# Границы яркости исходника: ниже первой всё считается фоном, выше второй --
# полностью непрозрачным. Значения подобраны по самому изображению: рамка
# монитора тёмная, но заметно светлее залитого чёрным фона, а свечение
# вокруг ещё ярче.
ALPHA_FLOOR = 8
ALPHA_FULL = 52

# Какую долю иконки занимает изображение: остальное -- поля, чтобы свечение
# не упиралось в край.
CONTENT_SHARE = 0.96


def load_source(directory):
    """Загружает исходник и делает фон прозрачным."""
    path = directory / SOURCE
    if not path.exists():
        raise SystemExit(f'Не найден исходник {path}')

    # Ограничение на размер отключено: исходник большой, и PIL иначе
    # отказывается его открывать.
    Image.MAX_IMAGE_PIXELS = None
    source = Image.open(path).convert('RGB')

    # Обрезаем подпись.
    source = source.crop((0, 0, source.width, min(WATERMARK_TOP, source.height)))

    # Прозрачность из яркости. Это только маска: цвет пикселей берётся из
    # исходника как есть. Если делать прозрачными сами пиксели, свечение
    # просвечивает фоном и иконка выглядит выцветшей.
    gray = source.convert('L')

    def to_alpha(value):
        if value <= ALPHA_FLOOR:
            return 0
        if value >= ALPHA_FULL:
            return 255
        return int((value - ALPHA_FLOOR) * 255 / (ALPHA_FULL - ALPHA_FLOOR))

    alpha = gray.point(to_alpha)

    # Внутри монитора всё непрозрачно, включая тёмные части рамки: они должны
    # быть чёрными, а не просвечивать. Для этого силуэт заливается изнутри --
    # берём маску, убираем в ней мелкие разрывы и складываем с исходной.
    silhouette = alpha.point(lambda value: 255 if value > 40 else 0)
    silhouette = silhouette.filter(ImageFilter.MaxFilter(31))
    silhouette = silhouette.filter(ImageFilter.MinFilter(31))
    alpha = Image.composite(Image.new('L', alpha.size, 255), alpha, silhouette)

    # Края свечения смягчаются: резкого обрыва на границе быть не должно.
    alpha = alpha.filter(ImageFilter.GaussianBlur(radius=2))

    result = source.convert('RGBA')
    result.putalpha(alpha)

    # Обрезаем по непрозрачной части: так изображение вписывается плотно,
    # без лишних полей от исходного квадрата.
    box = result.getbbox()
    return result.crop(box) if box else result


def build_icon(source, size):
    """Вписывает изображение в квадратную иконку заданного размера."""
    content = int(size * CONTENT_SHARE)

    # Вписываем по большей стороне, сохраняя пропорции.
    scale = content / max(source.width, source.height)
    target = (max(1, int(source.width * scale)), max(1, int(source.height * scale)))

    resized = source.resize(target, Image.LANCZOS)

    canvas = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    canvas.alpha_composite(resized,
                           ((size - target[0]) // 2, (size - target[1]) // 2))
    return canvas


def main():
    out_dir = Path(__file__).parent
    source = load_source(out_dir)

    print(f'исходник после обработки: {source.size}')

    images = [build_icon(source, size) for size in SIZES]

    # Крупная версия как PNG: удобно смотреть и использовать в документации.
    images[-1].save(out_dir / 'icon.png', format='PNG')

    # В .ico кладём основные размеры; 20 и 40 нужны редко, а файл от них
    # только растёт.
    keep = [i for i, size in enumerate(SIZES) if size not in (20, 40)]
    ico_sizes = [(SIZES[i], SIZES[i]) for i in keep]

    images[keep[-1]].save(
        out_dir / 'icon.ico',
        format='ICO',
        sizes=ico_sizes,
        append_images=[images[i] for i in keep[:-1]],
    )

    print(f'иконка сохранена: {out_dir / "icon.ico"}')
    print(f'крупная версия:   {out_dir / "icon.png"}')
    print(f'размеры в .ico:   {", ".join(str(s) for s, _ in ico_sizes)}')


if __name__ == '__main__':
    main()
