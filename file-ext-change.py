import os

target_dir = "."

for path, dirs, files in os.walk(target_dir):
    for file in files:
        filename, ext = os.path.splitext(file)
        new_file = filename + ".txt"

        if ext == '.md':
            old_filepath = os.path.join(path, file)
            new_filepath = os.path.join(path, new_file)
            os.rename(old_filepath, new_filepath)

