import os
from pathlib import Path
import sys
import shutil

target_dir = Path(sys.argv[1])
project_dir = Path(sys.argv[2])
version = sys.argv[3]


def copy_virtual_dualsense_driver(repo_dir: Path, package_dir: Path):
    driver_dir = repo_dir / "VirtualDualSenseDriver"
    if not driver_dir.exists():
        return

    package_driver_dir = package_dir / "VirtualDualSenseDriver"
    if package_driver_dir.exists():
        shutil.rmtree(package_driver_dir)

    package_driver_dir.mkdir()

    for file_name in [
        "README.md",
        "FOLLOW_UP.md",
        "HBashtonVirtualDualSense.inf",
        "install-driver.ps1",
        "check-driver.ps1",
    ]:
        source_file = driver_dir / file_name
        if source_file.exists():
            shutil.copy2(source_file, package_driver_dir / file_name)

    for pattern in [
        "HBashtonVirtualDualSense.sys",
        "HBashtonVirtualDualSense.cat",
        "HBashtonVirtualDualSense.cer",
        "HBashtonVirtualDualSense.pdb",
    ]:
        for source_file in driver_dir.rglob(pattern):
            if any(part.lower() in ("obj", "ipch") for part in source_file.parts):
                continue

            shutil.copy2(source_file, package_driver_dir / source_file.name)
            break

# move l18n assemblies to a separate directory
lang_dir = target_dir / "Lang"
if not lang_dir.exists():
    Path.mkdir(lang_dir)

langs = ["ar", "cs", "de", "el", "es", "fi", "fr", "he", "hu-HU", "idn", "it", "ja", "ms",
         "nl", "pl", "pt", "pt-BR", "ru", "se", "tr", "uk-UA", "vi", "zh-Hans", "zh-Hant", "zh-CN"]
for lang in langs:
    current_lang_dir = target_dir / lang
    target_lang_dir = lang_dir / lang
    if not target_lang_dir.exists():
        target_lang_dir.mkdir()

    if current_lang_dir.exists():
        for file in current_lang_dir.iterdir():
            if file.is_file():
                shutil.move(file, target_lang_dir / file.name)
        current_lang_dir.rmdir()


# run the script injecting new dependency paths to DS4Windows.deps.json
lang_script = project_dir.parent / "utils" / "inject_deps_path.py"
deps_json_path = target_dir / "DS4Windows.deps.json"
os.system(f"python {lang_script} {deps_json_path}")


# write the version to newest.txt
newest_txt = project_dir / "newest.txt"
with open(newest_txt, 'w') as file:
    file.write(version)


# rename target dir (net8.0-windows) to DS4Windows
renamed_dir = target_dir.parent / "DS4Windows"
if renamed_dir.exists():
    shutil.rmtree(renamed_dir)

os.rename(target_dir, renamed_dir)
copy_virtual_dualsense_driver(project_dir.resolve(), renamed_dir)

# create a zip
arch = target_dir.parents[1].name
zip_name = f"DS4Windows_{version}_{arch}"
target_zip_path = target_dir.parent / f"{zip_name}.zip"
if target_zip_path.exists():
    os.remove(target_zip_path)

zip_dir = shutil.make_archive(zip_name, "zip", target_dir.parent)

# move the zip to the build directory
shutil.move(zip_dir, target_zip_path)
