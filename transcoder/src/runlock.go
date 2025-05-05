package src

import (
	"errors"
	"log"
	"sync"
)

type RunLock[K comparable, V any] struct {
	running map[K]*Task[V]
	lock    sync.Mutex
}

type Task[V any] struct {
	ready     sync.WaitGroup
	listeners []chan Result[V]
}

type Result[V any] struct {
	ok  V
	err error
}

func NewRunLock[K comparable, V any]() RunLock[K, V] {
	return RunLock[K, V]{
		running: make(map[K]*Task[V]),
	}
}

func (r *RunLock[K, V]) Start(key K) (func() (V, error), func(val V, err error) (V, error)) {
	r.lock.Lock()
	defer r.lock.Unlock()
	task, ok := r.running[key]

	if ok {
		log.Printf("Task %v is already running", key)
		// Important: Buffer this so that listener notifications are not blocked
		// when the job completes.
		ret := make(chan Result[V], 1)
		task.listeners = append(task.listeners, ret)
		return func() (V, error) {
			log.Printf("Waiting for task %v to finish", key)
			res := <-ret
			log.Printf("Task %v finished with result: %v, %v", key, res.ok, res.err)
			return res.ok, res.err
		}, nil
	}

	r.running[key] = &Task[V]{
		listeners: make([]chan Result[V], 0),
	}

	log.Printf("Returning new task %v", key)
	return nil, func(val V, err error) (V, error) {
		log.Printf("Attempting to take lock for task %v", key)
		r.lock.Lock()
		defer r.lock.Unlock()
		log.Printf("Lock taken for task %v", key)

		task, ok = r.running[key]
		if !ok {
			return val, errors.New("invalid run lock state. aborting.")
		}

		log.Printf("Notifying %d listeners for task %v", len(task.listeners), key)
		for _, listener := range task.listeners {
			listener <- Result[V]{ok: val, err: err}
			close(listener)
		}
		log.Printf("Finished notifying listeners for task %v", key)

		delete(r.running, key)
		return val, err
	}
}

func (r *RunLock[K, V]) WaitFor(key K) (V, error) {
	r.lock.Lock()
	task, ok := r.running[key]

	if !ok {
		r.lock.Unlock()
		var val V
		return val, nil
	}

	ret := make(chan Result[V])
	task.listeners = append(task.listeners, ret)

	r.lock.Unlock()
	res := <-ret
	return res.ok, res.err
}
