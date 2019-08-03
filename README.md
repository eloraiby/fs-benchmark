# F# Async Socket Benchmark

The purpose of this project is to measure the latecny of async sockets in F#/.Net (on Linux, using dotnet core)

Thanks to Adgear/Samsung Ads for making it public

## Dependencies
on Ubuntu (for the hammer):
```
sudo apt-get install libuv1-dev
```

## Building
Hammer:
```
$ cd uv-hammer
$ mkdir build
$ cd build
$ cmake ..
$ make -j4
$ make install
```

fs-async-server:
```
$ cd fs-async-server
$ dotnet build
```

## Running

First run the server:
```
fs-async-server$ dotnet run
Started listening on port 55555
Waiting for key to exit
```

Then run the hammer from another server:
```
 $ uv-hammer -i 123.123.123.123 -p 55555 -t 4 -c 4 -n 8
sending: CB 1234:4567 1234:4567 1234:4567 1234:4567 1234:4567 1234:4567 1234:4567 1234:4567
request per packet: 8
Connected...
Connected...
Connected...
...
```

Where:
```
-p : port to which to connect
-t : number of threads to run in parallel
-c : number of clients per thread
-n : number of requests per packet
```



