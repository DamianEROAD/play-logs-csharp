#!/bin/bash

for filename in Logs/*; do
    numbersOnly=$(echo $filename | cut -d'-' -f 2 | cut -d'.' -f 1 | cut -d' ' -f 1 | sed -e s/[^0-9]//g)
    noNumbers=$(echo $filename | cut -d'.' -f 1 | cut -d'/' -f 2 | sed -e s/[0-9]//g | sed -e s/\-//g)
    prefix=$(echo $filename | cut -d'/' -f 1)
    suffix=$(echo $filename | cut -d'.' -f 2-5)
    mv $filename "${prefix}/${numbersOnly}-${noNumbers}.${suffix}"
done
